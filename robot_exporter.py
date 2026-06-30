import os
import sys
import json
import zipfile
import xml.etree.ElementTree as ET
from xml.dom import minidom
from pxr import Usd, UsdGeom, UsdPhysics, Gf, Sdf

def prettify_xml(elem):
    """
    Returns a pretty-printed XML string for the URDF.
    """
    rough_string = ET.tostring(elem, 'utf-8')
    reparsed = minidom.parseString(rough_string)
    return reparsed.toprettyxml(indent="  ")

def build_urdf(config, mesh_rel_dir, output_filepath):
    """
    Generates a valid ROS URDF XML file from the robot config.
    """
    robot_name = config.get("robot_name", "AuraRobot")
    root = ET.Element("robot", name=robot_name)
    
    # 1. Write Links
    for link in config.get("links", []):
        link_elem = ET.SubElement(root, "link", name=link["name"])
        
        # Inertial properties
        inertial = ET.SubElement(link_elem, "inertial")
        ET.SubElement(inertial, "mass", value=str(link["mass"]))
        com = link.get("center_of_mass", [0.0, 0.0, 0.0])
        ET.SubElement(inertial, "origin", xyz=f"{com[0]} {com[1]} {com[2]}", rpy="0 0 0")
        
        inertia = link.get("inertia", {})
        ET.SubElement(inertial, "inertia",
                      ixx=str(inertia.get("ixx", 1e-5)),
                      ixy=str(inertia.get("ixy", 0.0)),
                      ixz=str(inertia.get("ixz", 0.0)),
                      iyy=str(inertia.get("iyy", 1e-5)),
                      iyz=str(inertia.get("iyz", 0.0)),
                      izz=str(inertia.get("izz", 1e-5)))
        
        # Visual mesh representation
        if link.get("mesh_path"):
            visual = ET.SubElement(link_elem, "visual")
            ET.SubElement(visual, "origin", xyz="0 0 0", rpy="0 0 0")
            geom = ET.SubElement(visual, "geometry")
            # standard ROS convention uses package URL schema
            ET.SubElement(geom, "mesh", filename=f"package://{robot_name}/{link['mesh_path']}")
            
            # Collision representation
            collision = ET.SubElement(link_elem, "collision")
            ET.SubElement(collision, "origin", xyz="0 0 0", rpy="0 0 0")
            c_geom = ET.SubElement(collision, "geometry")
            ET.SubElement(c_geom, "mesh", filename=f"package://{robot_name}/{link['mesh_path']}")
            
    # 2. Write Joints
    for joint in config.get("joints", []):
        joint_elem = ET.SubElement(root, "joint", name=joint["name"], type=joint["type"])
        
        ET.SubElement(joint_elem, "parent", link=joint["parent"])
        ET.SubElement(joint_elem, "child", link=joint["child"])
        
        origin = joint.get("origin", {})
        xyz = origin.get("xyz", [0.0, 0.0, 0.0])
        rpy = origin.get("rpy", [0.0, 0.0, 0.0])
        ET.SubElement(joint_elem, "origin", xyz=f"{xyz[0]} {xyz[1]} {xyz[2]}", rpy=f"{rpy[0]} {rpy[1]} {rpy[2]}")
        
        axis = joint.get("axis", [0.0, 0.0, 1.0])
        ET.SubElement(joint_elem, "axis", xyz=f"{axis[0]} {axis[1]} {axis[2]}")
        
        if joint["type"] in ["revolute", "prismatic"]:
            limits = joint.get("limits", {})
            ET.SubElement(joint_elem, "limit",
                          lower=str(limits.get("lower", -3.14)),
                          upper=str(limits.get("upper", 3.14)),
                          effort=str(limits.get("effort", 10.0)),
                          velocity=str(limits.get("velocity", 1.5)))
            
    # Save formatted XML
    xml_str = prettify_xml(root)
    with open(output_filepath, "w", encoding="utf-8") as f:
        f.write(xml_str)

import re

def sanitize_name(name):
    """
    Cleans names to be valid USD SdfPath elements (no digits at start, no hyphens).
    """
    sanitized = re.sub(r'[^a-zA-Z0-9_]', '_', name)
    if sanitized and sanitized[0].isdigit():
        sanitized = 'r_' + sanitized
    return sanitized

def build_usd(config, output_filepath):
    """
    Generates Pixar USD/USDA format robot kinematics with USD Physics Schemas.
    """
    stage = Usd.Stage.CreateNew(output_filepath)
    UsdGeom.SetStageUpAxis(stage, UsdGeom.Tokens.z)
    
    robot_name = sanitize_name(config.get("robot_name", "AuraRobot"))
    # Define root Articulation Root
    robot_path = f"/{robot_name}"
    robot_prim = UsdGeom.Xform.Define(stage, robot_path)
    stage.SetDefaultPrim(robot_prim.GetPrim())
    
    # Apply Articulation Root for Isaac Sim/PhysX solver compatibility
    UsdPhysics.ArticulationRootAPI.Apply(robot_prim.GetPrim())
    
    # 1. Define Xforms for Links and bind mass inertias
    for link in config.get("links", []):
        link_name = sanitize_name(link["name"])
        link_path = f"{robot_path}/{link_name}"
        link_xform = UsdGeom.Xform.Define(stage, link_path)
        
        # Set physics mass API
        mass_api = UsdPhysics.MassAPI.Apply(link_xform.GetPrim())
        mass_api.CreateMassAttr(float(link["mass"]))
        
        com = link.get("center_of_mass", [0.0, 0.0, 0.0])
        mass_api.CreateCenterOfMassAttr(Gf.Vec3f(com[0], com[1], com[2]))
        
        inertia = link.get("inertia", {})
        mass_api.CreateDiagonalInertiaAttr(Gf.Vec3f(
            float(inertia.get("ixx", 1e-5)),
            float(inertia.get("iyy", 1e-5)),
            float(inertia.get("izz", 1e-5))
        ))
        
        # Link visual mesh reference (pointing to STL meshes relative to USD stage)
        if link.get("mesh_path"):
            mesh_path = f"{link_path}/visual_mesh"
            # Define as Mesh or Xform with references
            mesh_prim = UsdGeom.Xform.Define(stage, mesh_path)
            mesh_prim.GetPrim().GetReferences().AddReference(f"./{link['mesh_path']}")
            
            # Apply collision geometry API if needed
            UsdPhysics.CollisionAPI.Apply(mesh_prim.GetPrim())

    # 2. Define Physics Joint relations
    for joint in config.get("joints", []):
        joint_name = sanitize_name(joint["name"])
        joint_path = f"{robot_path}/{joint_name}"
        joint_type = joint["type"]
        
        parent_path = f"{robot_path}/{sanitize_name(joint['parent'])}"
        child_path = f"{robot_path}/{sanitize_name(joint['child'])}"
        
        # Deconstruct origin offsets
        origin = joint.get("origin", {})
        xyz = origin.get("xyz", [0.0, 0.0, 0.0])
        rpy = origin.get("rpy", [0.0, 0.0, 0.0])
        
        # Choose specific USD joint class
        if joint_type == "fixed":
            usd_joint = UsdPhysics.FixedJoint.Define(stage, joint_path)
        elif joint_type in ["revolute", "continuous"]:
            usd_joint = UsdPhysics.RevoluteJoint.Define(stage, joint_path)
            
            # Map Axis (e.g., Z rotation)
            axis = joint.get("axis", [0.0, 0.0, 1.0])
            axis_token = "Z"
            if abs(axis[0]) > 0.9: axis_token = "X"
            elif abs(axis[1]) > 0.9: axis_token = "Y"
            usd_joint.CreateAxisAttr(axis_token)
            
            # Set rotational limits (converted to degrees for USD Physics spec)
            if joint_type == "revolute":
                limits = joint.get("limits", {})
                usd_joint.CreateLowerLimitAttr(float(limits.get("lower", -3.14)) * 180.0 / 3.14159)
                usd_joint.CreateUpperLimitAttr(float(limits.get("upper", 3.14)) * 180.0 / 3.14159)
        elif joint_type == "prismatic":
            usd_joint = UsdPhysics.PrismaticJoint.Define(stage, joint_path)
            axis = joint.get("axis", [0.0, 0.0, 1.0])
            axis_token = "Z"
            if abs(axis[0]) > 0.9: axis_token = "X"
            elif abs(axis[1]) > 0.9: axis_token = "Y"
            usd_joint.CreateAxisAttr(axis_token)
            
            limits = joint.get("limits", {})
            usd_joint.CreateLowerLimitAttr(float(limits.get("lower", -0.5)))
            usd_joint.CreateUpperLimitAttr(float(limits.get("upper", 0.5)))
        else:
            # Fallback to Fixed Joint
            usd_joint = UsdPhysics.FixedJoint.Define(stage, joint_path)
            
        # Assign targets
        usd_joint.CreateBody0Rel().SetTargets([Sdf.Path(parent_path)])
        usd_joint.CreateBody1Rel().SetTargets([Sdf.Path(child_path)])
        
        # Origin offset relative to parents frame
        usd_joint.CreateLocalPos0Attr(Gf.Vec3f(xyz[0], xyz[1], xyz[2]))
        # Simple rotation offset mapping via quaternions or matrix
        q = Gf.Rotation(Gf.Vec3d(1,0,0), rpy[0]*180/3.14).GetQuaternion()
        usd_joint.CreateLocalRot0Attr(Gf.Quatf(q.GetReal(), Gf.Vec3f(q.GetImaginary())))
        
        # Local pos on child is typically identity unless offsets are set
        usd_joint.CreateLocalPos1Attr(Gf.Vec3f(0.0, 0.0, 0.0))
        usd_joint.CreateLocalRot1Attr(Gf.Quatf(1.0, 0.0, 0.0, 0.0))

    # Save compile
    stage.GetRootLayer().Save()

def create_zip_package(source_dir, output_zip_path):
    """
    Creates a zip archive of the converted files.
    """
    with zipfile.ZipFile(output_zip_path, 'w', zipfile.ZIP_DEFLATED) as zipf:
        for root, _, files in os.walk(source_dir):
            for file in files:
                file_path = os.path.join(root, file)
                # Keep original folder structure inside zip
                rel_path = os.path.relpath(file_path, source_dir)
                zipf.write(file_path, rel_path)

def main():
    if len(sys.argv) < 3:
        print("Usage: python robot_exporter.py <config_json_path> <output_directory>", file=sys.stderr)
        sys.exit(1)
        
    config_json_path = sys.argv[1]
    output_dir = sys.argv[2]
    
    if not os.path.exists(config_json_path):
        print(f"Error: Config file not found: {config_json_path}", file=sys.stderr)
        sys.exit(1)
        
    with open(config_json_path, "r", encoding="utf-8") as f:
        config = json.load(f)
        
    robot_name = config.get("robot_name", "AuraRobot")
    
    # 1. Output URDF XML
    urdf_filename = f"{robot_name}.urdf"
    urdf_filepath = os.path.join(output_dir, urdf_filename)
    print(f"[Exporter] Generating URDF file: {urdf_filepath} ...")
    build_urdf(config, "meshes", urdf_filepath)
    
    # 2. Output USD USDA Scene
    usd_filename = f"{robot_name}.usda"
    usd_filepath = os.path.join(output_dir, usd_filename)
    print(f"[Exporter] Generating USD file: {usd_filepath} ...")
    build_usd(config, usd_filepath)
    
    # 3. Zip package
    zip_filename = f"{robot_name}_urdf_package.zip"
    zip_filepath = os.path.join(os.path.dirname(output_dir), zip_filename)
    print(f"[Exporter] Archiving package ZIP: {zip_filepath} ...")
    create_zip_package(output_dir, zip_filepath)
    
    print(f"[Success] Export complete. ZIP package ready at: {zip_filepath}")

if __name__ == "__main__":
    main()
