using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using AnyCAD.Foundation;
using Aura3DRobotConverter.Models;

namespace Aura3DRobotConverter.Services
{
    public class CsharpStepParser
    {
        public static RobotConfig ParseStepFile(string stepFilePath, string outputDir, double density = 2700.0)
        {
            string meshDir = System.IO.Path.Combine(outputDir, "meshes");
            Directory.CreateDirectory(meshDir);

            // Initialize AnyCAD Shape Reader
            TopoShape shape = ShapeIO.Open(stepFilePath);
            if (shape == null)
            {
                throw new Exception("AnyCAD engine failed to parse and load the STEP file.");
            }

            var links = new List<RobotLink>();
            var joints = new List<RobotJoint>();

            // Traverse and extract separate Solid bodies using FindChild index lookup
            var solids = new List<TopoShape>();
            int idx = 0;
            while (true)
            {
                var child = shape.FindChild(EnumTopoShapeType.Topo_SOLID, idx);
                if (child == null) break;
                solids.Add(child);
                idx++;
            }

            int solidId = 0;
            foreach (var subShape in solids)
            {
                string linkName = $"link_{solidId}";
                string meshFilename = $"{linkName}.stl";
                string meshPath = System.IO.Path.Combine(meshDir, meshFilename);

                // Tessellate sub-shape and save as STL mesh
                ShapeIO.Save(subShape, meshPath);

                // Integrate physical properties using tetrahedron math over mesh vertices
                var (mass, com, inertia) = ComputePhysicalProperties(meshPath, density);

                var linkEntry = new RobotLink
                {
                    Name = linkName,
                    MeshPath = $"meshes/{meshFilename}",
                    Mass = mass,
                    CenterOfMass = new double[] { com.X, com.Y, com.Z },
                    Inertia = new Models.Inertia
                    {
                        Ixx = inertia[0], Iyy = inertia[1], Izz = inertia[2],
                        Ixy = inertia[3], Ixz = inertia[4], Iyz = inertia[5]
                    }
                };
                links.Add(linkEntry);
                solidId++;
            }

            if (links.Count == 0)
            {
                throw new Exception("No valid rigid 3D solids detected inside the STEP file.");
            }

            // Rename root element link to base_link
            links[0].Name = "base_link";
            links[0].MeshPath = links[0].MeshPath.Replace("link_0.stl", "base_link.stl");
            string oldPath = System.IO.Path.Combine(meshDir, "link_0.stl");
            string newPath = System.IO.Path.Combine(meshDir, "base_link.stl");
            if (File.Exists(oldPath))
            {
                if (File.Exists(newPath)) File.Delete(newPath);
                File.Move(oldPath, newPath);
            }

            // Establish sequential Joints chain mapping using relative Center of Mass (CoM) coordinates
            for (int i = 1; i < links.Count; i++)
            {
                var parentLink = links[i - 1];
                var childLink = links[i];

                double[] pCom = parentLink.CenterOfMass;
                double[] cCom = childLink.CenterOfMass;
                double[] xyz = new double[] { cCom[0] - pCom[0], cCom[1] - pCom[1], cCom[2] - pCom[2] };

                var jointEntry = new RobotJoint
                {
                    Name = $"joint_{parentLink.Name}_to_{childLink.Name}",
                    Type = "revolute",
                    Parent = parentLink.Name,
                    Child = childLink.Name,
                    Origin = new JointOrigin
                    {
                        Xyz = xyz,
                        Rpy = new double[] { 0, 0, 0 }
                    },
                    Axis = new double[] { 0, 0, 1 },
                    Limits = new JointLimits
                    {
                        Lower = -3.1415,
                        Upper = 3.1415,
                        Effort = 10.0,
                        Velocity = 1.5
                    }
                };
                joints.Add(jointEntry);
            }

            return new RobotConfig
            {
                RobotName = System.IO.Path.GetFileNameWithoutExtension(stepFilePath),
                RootLink = "base_link",
                Links = links,
                Joints = joints
            };
        }

        private static (double Mass, Vector3D CoM, double[] Inertia) ComputePhysicalProperties(string stlPath, double density)
        {
            if (!File.Exists(stlPath))
            {
                return (0.1, new Vector3D(0, 0, 0), new double[] { 1e-5, 1e-5, 1e-5, 0, 0, 0 });
            }

            // Load mesh vertices using Helix Toolkit ModelImporter
            var importer = new ModelImporter();
            var modelGroup = importer.Load(stlPath);
            MeshGeometry3D? mesh = null;

            modelGroup.Traverse<GeometryModel3D>((geom, transform) =>
            {
                if (geom.Geometry is MeshGeometry3D m)
                {
                    mesh = m;
                }
            });

            if (mesh == null || mesh.Positions.Count < 3)
            {
                return (0.1, new Vector3D(0, 0, 0), new double[] { 1e-5, 1e-5, 1e-5, 0, 0, 0 });
            }

            double totalVolume = 0;
            var weightedCom = new Vector3D(0, 0, 0);

            double tempIxx = 0, tempIyy = 0, tempIzz = 0;
            double tempIxy = 0, tempIxz = 0, tempIyz = 0;

            var positions = mesh.Positions;
            var indices = mesh.TriangleIndices;

            for (int i = 0; i < indices.Count; i += 3)
            {
                var p0 = (Vector3D)positions[indices[i]];
                var p1 = (Vector3D)positions[indices[i + 1]];
                var p2 = (Vector3D)positions[indices[i + 2]];

                // Tetrahedron volume equation relative to (0,0,0) and the triangular face
                double v = Vector3D.DotProduct(p0, Vector3D.CrossProduct(p1, p2)) / 6.0;
                totalVolume += v;

                var centroid = (p0 + p1 + p2) / 4.0;
                weightedCom += centroid * v;

                double x_avg = (p0.X + p1.X + p2.X) / 3.0;
                double y_avg = (p0.Y + p1.Y + p2.Y) / 3.0;
                double z_avg = (p0.Z + p1.Z + p2.Z) / 3.0;

                tempIxx += (y_avg * y_avg + z_avg * z_avg) * v;
                tempIyy += (x_avg * x_avg + z_avg * z_avg) * v;
                tempIzz += (x_avg * x_avg + y_avg * y_avg) * v;

                tempIxy -= (x_avg * y_avg) * v;
                tempIxz -= (x_avg * z_avg) * v;
                tempIyz -= (y_avg * z_avg) * v;
            }

            if (Math.Abs(totalVolume) < 1e-9)
            {
                return (0.001, new Vector3D(0, 0, 0), new double[] { 1e-5, 1e-5, 1e-5, 0, 0, 0 });
            }

            var com = weightedCom / totalVolume; // com in mm
            double volumeM3 = totalVolume / 1e9; // scale volume to m^3
            double mass = Math.Max(volumeM3 * density, 0.001); // mass in kg

            // Apply parallel axis theorem shift to align moments relative to center of mass
            double scale = density / 1e15; // scale mm^5 to kg*m^2
            double ixx = Math.Max(tempIxx * scale - mass * (com.Y * com.Y + com.Z * com.Z) / 1e6, 1e-5);
            double iyy = Math.Max(tempIyy * scale - mass * (com.X * com.X + com.Z * com.Z) / 1e6, 1e-5);
            double izz = Math.Max(tempIzz * scale - mass * (com.X * com.X + com.Y * com.Y) / 1e6, 1e-5);

            double ixy = tempIxy * scale + mass * (com.X * com.Y) / 1e6;
            double ixz = tempIxz * scale + mass * (com.X * com.Z) / 1e6;
            double iyz = tempIyz * scale + mass * (com.Y * com.Z) / 1e6;

            var comMeters = com / 1000.0; // scale CoM to meters

            return (mass, comMeters, new double[] { ixx, iyy, izz, ixy, ixz, iyz });
        }
    }
}
