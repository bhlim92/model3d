import os
import sys
import json
import uuid
import cadquery as cq
import numpy as np

def parse_transform(loc):
    """
    Extracts the relative transformation matrix from a CadQuery location.
    """
    if loc is None:
        return np.eye(4).tolist()
    
    # Get 4x4 homogenous transformation matrix from OCC Location
    occ_trans = loc.wrapped.Transformation()
    m = []
    for r in range(1, 4):
        row = []
        for c in range(1, 5):
            row.append(occ_trans.Value(r, c))
        m.append(row)
    m.append([0.0, 0.0, 0.0, 1.0])
    return m

def matrix_to_xyz_rpy(matrix):
    """
    Decomposes a 4x4 matrix into translation (xyz) and rotation (rpy - Roll, Pitch, Yaw).
    """
    m = np.array(matrix)
    xyz = m[:3, 3].tolist()
    
    # Calculate RPY (intrinsic ZYX or extrinsic XYZ Tait-Bryan angles)
    r11, r12, r13 = m[0, 0], m[0, 1], m[0, 2]
    r21, r22, r23 = m[1, 0], m[1, 1], m[1, 2]
    r31, r32, r33 = m[2, 0], m[2, 1], m[2, 2]
    
    # Pitch (beta)
    pitch = np.arctan2(-r31, np.sqrt(r32**2 + r33**2))
    
    # Roll (alpha) & Yaw (gamma)
    if np.abs(pitch - np.pi/2) < 1e-5:
        roll = 0.0
        yaw = np.arctan2(r12, r22)
    elif np.abs(pitch + np.pi/2) < 1e-5:
        roll = 0.0
        yaw = -np.arctan2(r12, r22)
    else:
        roll = np.arctan2(r32, r33)
        yaw = np.arctan2(r21, r11)
        
    return xyz, [float(roll), float(pitch), float(yaw)]

from OCP.GProp import GProp_GProps
from OCP.BRepGProp import BRepGProp

def compute_inertial_properties(shape, density=2700.0):
    """
    Computes mass, center of mass, and geometric inertia tensor directly in memory 
    using OpenCASCADE (OCP) C++ engine. Bypasses slow disk I/O.
    """
    props = GProp_GProps()
    BRepGProp.VolumeProperties_s(shape.wrapped, props)
    
    # Geometric volume in mm^3
    volume = props.Mass()
    # Mass in kg (volume in m^3 * density in kg/m^3)
    mass = max(volume * density / 1e9, 0.001)
    
    # Centre of Mass in mm -> scaled to meters
    occ_com = props.CentreOfMass()
    com = [occ_com.X() / 1000.0, occ_com.Y() / 1000.0, occ_com.Z() / 1000.0]
    
    # Matrix of Inertia (Geometric moment in mm^5) -> scaled to kg * m^2
    # Conversion: mm^5 / 10^15 = m^5. Then multiplied by density (kg/m^3) to yield kg*m^2.
    mat = props.MatrixOfInertia()
    ixx = max(mat.Value(1, 1) * density / 1e15, 1e-5)
    iyy = max(mat.Value(2, 2) * density / 1e15, 1e-5)
    izz = max(mat.Value(3, 3) * density / 1e15, 1e-5)
    ixy = -mat.Value(1, 2) * density / 1e15
    ixz = -mat.Value(1, 3) * density / 1e15
    iyz = -mat.Value(2, 3) * density / 1e15
    
    return mass, com, {
        "ixx": ixx, "iyy": iyy, "izz": izz,
        "ixy": ixy, "ixz": ixz, "iyz": iyz
    }

def process_node(assembly, output_mesh_dir, density=2700.0):
    """
    Recursively processes CadQuery assembly nodes to build tree hierarchy.
    """
    node_id = assembly.name if assembly.name else str(uuid.uuid4())[:8]
    node_data = {
        "name": node_id,
        "type": "link",
        "transform": parse_transform(assembly.loc),
        "children": []
    }
    
    # Handle physical parts with shape geometry
    shape = assembly.obj
    if shape:
        if hasattr(shape, "val"):
            shape = shape.val()
            
        if shape:
            # Export STL mesh for visualization (single disk write, no reads)
            mesh_filename = f"{node_id}.stl"
            mesh_filepath = os.path.join(output_mesh_dir, mesh_filename)
            
            try:
                cq.exporters.export(shape, mesh_filepath, cq.exporters.ExportTypes.STL)
                
                # Compute mass parameters directly in memory using OCP C++ engine
                mass, com, inertia = compute_inertial_properties(shape, density)
                
                node_data["geometry"] = {
                    "mesh_path": f"meshes/{mesh_filename}",
                    "mass": mass,
                    "center_of_mass": com,
                    "inertia": inertia
                }
            except Exception as e:
                print(f"[Warning] Failed to process geometry for {node_id}: {str(e)}", file=sys.stderr)
            
    # Process child assembly joints and links
    for child in assembly.children:
        child_data = process_node(child, output_mesh_dir, density)
        node_data["children"].append(child_data)
        
    return node_data

def flatten_and_create_joints(node, parent_name=None, accum_transform=None, links=None, joints=None):
    """
    Flattens the assembly tree structure and accumulates relative transforms.
    Converts joint coordinates from mm to meters.
    """
    if links is None: links = []
    if joints is None: joints = []
    
    # Current relative transform matrix
    curr_t = np.array(node["transform"])
    
    if accum_transform is None:
        accum_t = curr_t
    else:
        # Multiply parent accumulated transform with current relative transform
        accum_t = np.dot(accum_transform, curr_t)
        
    link_name = node["name"]
    xyz, rpy = matrix_to_xyz_rpy(accum_t)
    
    link_entry = {
        "name": link_name,
        "mesh_path": node.get("geometry", {}).get("mesh_path", ""),
        "mass": node.get("geometry", {}).get("mass", 0.0),
        "center_of_mass": node.get("geometry", {}).get("center_of_mass", [0.0, 0.0, 0.0]),
        "inertia": node.get("geometry", {}).get("inertia", {
            "ixx": 1e-5, "iyy": 1e-5, "izz": 1e-5,
            "ixy": 0.0, "ixz": 0.0, "iyz": 0.0
        })
    }
    
    # We only register as link if it has a physical visual mesh geometry
    if link_entry["mesh_path"] or parent_name is None:
        links.append(link_entry)
        
        # If it has a parent link, we establish a kinematic Joint relationship
        if parent_name:
            joint_entry = {
                "name": f"joint_{parent_name}_to_{link_name}",
                "type": "revolute", # default type, editable in Web UI
                "parent": parent_name,
                "child": link_name,
                "origin": {
                    # Convert origin from mm (CAD standard) to meters (URDF/USD/Simulation standard)
                    "xyz": [xyz[0] / 1000.0, xyz[1] / 1000.0, xyz[2] / 1000.0],
                    "rpy": rpy
                },
                "axis": [0.0, 0.0, 1.0], # default Z-axis rotation
                "limits": {
                    "lower": -3.1415,
                    "upper": 3.1415,
                    "effort": 10.0,
                    "velocity": 1.5
                }
            }
            joints.append(joint_entry)
            
        current_parent = link_name
        # Once a physical link frame is established, next children transforms will be relative to it
        next_accum = np.eye(4)
    else:
        current_parent = parent_name
        # Skip dummy group and pass accumulated transform down
        next_accum = accum_t
        
    for child in node["children"]:
        flatten_and_create_joints(child, current_parent, next_accum, links, joints)
        
    return links, joints

def main():
    if len(sys.argv) < 3:
        print("Usage: python step_parser.py <step_file_path> <output_directory>", file=sys.stderr)
        sys.exit(1)
        
    step_path = sys.argv[1]
    output_dir = sys.argv[2]
    
    if not os.path.exists(step_path):
        print(f"Error: File not found: {step_path}", file=sys.stderr)
        sys.exit(1)
        
    mesh_dir = os.path.join(output_dir, "meshes")
    os.makedirs(mesh_dir, exist_ok=True)
    
    print(f"[Parser] Importing CAD STEP file: {step_path} ...")
    try:
        assembly = cq.Assembly.load(step_path)
    except Exception as e:
        print(f"Error importing STEP file via CadQuery: {str(e)}", file=sys.stderr)
        sys.exit(1)
        
    print("[Parser] Tessellating assembly structure and computing mass parameters...")
    raw_tree = process_node(assembly, mesh_dir)
    
    # Flatten the hierarchical tree structure into links and joints
    links, joints = flatten_and_create_joints(raw_tree)
    
    assembly_json = {
        "robot_name": os.path.splitext(os.path.basename(step_path))[0],
        "root_link": links[0]["name"] if links else "base_link",
        "links": links,
        "joints": joints
    }
    
    # Write structural manifest file
    json_path = os.path.join(output_dir, "assembly_structure.json")
    with open(json_path, "w", encoding="utf-8") as f:
        json.dump(assembly_json, f, indent=2, ensure_ascii=False)
        
    print(f"[Success] Structural schema JSON exported to: {json_path}")
    print(f"[Success] Tessellated {len(links)} link meshes to: {mesh_dir}")

if __name__ == "__main__":
    main()
