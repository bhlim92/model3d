import os
import sys
import json
import uuid
import cadquery as cq
import trimesh
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
        # If wrapped inside a Workplane, extract the core Shape object
        if hasattr(shape, "val"):
            shape = shape.val()
            
        if shape:
            # Export temporary STL for triangulation & inertial computing
            mesh_filename = f"{node_id}.stl"
            mesh_filepath = os.path.join(output_mesh_dir, mesh_filename)
            
            try:
                # Tessellate CAD B-Rep shape to STL mesh
                cq.exporters.export(shape, mesh_filepath, cq.exporters.ExportTypes.STL)
                
                # Use trimesh to compute physical properties from triangulated mesh
                mesh = trimesh.load(mesh_filepath)
                if mesh.is_watertight:
                    volume = mesh.volume
                    mass = volume * density # kg
                    com = mesh.center_mass.tolist() # CoM origin
                    inertia_tensor = (mesh.moment_inertia * density / 1e9).tolist() # scaled to kg*m^2
                else:
                    # Fallback if triangulation has non-manifold edges
                    volume = shape.Volume()
                    mass = volume * density / 1e9 # convert mm^3 to m^3 representation
                    com = [0.0, 0.0, 0.0]
                    inertia_tensor = [[0.001, 0, 0], [0, 0.001, 0], [0, 0, 0.001]]
                
                node_data["geometry"] = {
                    "mesh_path": f"meshes/{mesh_filename}",
                    "mass": max(mass, 0.001),
                    "center_of_mass": com,
                    "inertia": {
                        "ixx": max(inertia_tensor[0][0], 1e-5),
                        "iyy": max(inertia_tensor[1][1], 1e-5),
                        "izz": max(inertia_tensor[2][2], 1e-5),
                        "ixy": inertia_tensor[0][1],
                        "ixz": inertia_tensor[0][2],
                        "iyz": inertia_tensor[1][2]
                    }
                }
            except Exception as e:
                print(f"[Warning] Failed to export or parse mesh for {node_id}: {str(e)}", file=sys.stderr)
            
    # Process child assembly joints and links
    for child in assembly.children:
        child_data = process_node(child, output_mesh_dir, density)
        node_data["children"].append(child_data)
        
    return node_data

def flatten_and_create_joints(node, parent_name=None, links=[], joints=[]):
    """
    Flattens the assembly tree structure and infers joint links coordinates.
    """
    link_name = node["name"]
    xyz, rpy = matrix_to_xyz_rpy(node["transform"])
    
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
                    "xyz": xyz,
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
    else:
        # Dummy group links pass down their parent relationship
        current_parent = parent_name
        
    for child in node["children"]:
        flatten_and_create_joints(child, current_parent, links, joints)
        
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
