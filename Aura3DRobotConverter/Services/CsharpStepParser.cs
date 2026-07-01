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
            if (modelGroup == null)
            {
                return (0.1, new Vector3D(0, 0, 0), new double[] { 1e-5, 1e-5, 1e-5, 0, 0, 0 });
            }
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

        public static RobotConfig ParseUrdfFile(string urdfPath)
        {
            var doc = System.Xml.Linq.XDocument.Load(urdfPath);
            var xRoot = doc.Root;
            if (xRoot == null || xRoot.Name != "robot")
            {
                throw new Exception("Invalid URDF format. The root element must be <robot>.");
            }

            string robotName = xRoot.Attribute("name")?.Value ?? "ImportedRobot";
            var links = new List<RobotLink>();
            var joints = new List<RobotJoint>();

            // Parse Links
            foreach (var xLink in xRoot.Elements("link"))
            {
                string linkName = xLink.Attribute("name")?.Value ?? "unknown_link";
                double mass = 0.001;
                double[] com = { 0, 0, 0 };
                var inertia = new Models.Inertia { Ixx = 1e-5, Iyy = 1e-5, Izz = 1e-5 };

                var xInertial = xLink.Element("inertial");
                if (xInertial != null)
                {
                    double.TryParse(xInertial.Element("mass")?.Attribute("value")?.Value, out mass);
                    
                    string? xyzStr = xInertial.Element("origin")?.Attribute("xyz")?.Value;
                    if (!string.IsNullOrEmpty(xyzStr))
                    {
                        var tokens = xyzStr.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (tokens.Length >= 3)
                        {
                            double.TryParse(tokens[0], out com[0]);
                            double.TryParse(tokens[1], out com[1]);
                            double.TryParse(tokens[2], out com[2]);
                        }
                    }

                    var xInertia = xInertial.Element("inertia");
                    if (xInertia != null)
                    {
                        double.TryParse(xInertia.Attribute("ixx")?.Value, out double ixx);
                        double.TryParse(xInertia.Attribute("ixy")?.Value, out double ixy);
                        double.TryParse(xInertia.Attribute("ixz")?.Value, out double ixz);
                        double.TryParse(xInertia.Attribute("iyy")?.Value, out double iyy);
                        double.TryParse(xInertia.Attribute("iyz")?.Value, out double iyz);
                        double.TryParse(xInertia.Attribute("izz")?.Value, out double izz);

                        inertia = new Models.Inertia
                        {
                            Ixx = ixx, Ixy = ixy, Ixz = ixz,
                            Iyy = iyy, Iyz = iyz, Izz = izz
                        };
                    }
                }

                string meshPath = string.Empty;
                var xMesh = xLink.Element("visual")?.Element("geometry")?.Element("mesh");
                if (xMesh != null)
                {
                    string filename = xMesh.Attribute("filename")?.Value ?? string.Empty;
                    if (filename.StartsWith("package://"))
                    {
                        int slashIndex = filename.IndexOf('/', 10);
                        if (slashIndex != -1)
                        {
                            meshPath = filename.Substring(slashIndex + 1);
                        }
                    }
                    else
                    {
                        meshPath = filename;
                    }
                }

                links.Add(new RobotLink
                {
                    Name = linkName,
                    MeshPath = meshPath,
                    Mass = mass,
                    CenterOfMass = com,
                    Inertia = inertia
                });
            }

            // Parse Joints
            foreach (var xJoint in xRoot.Elements("joint"))
            {
                string jointName = xJoint.Attribute("name")?.Value ?? "unknown_joint";
                string type = xJoint.Attribute("type")?.Value ?? "fixed";
                string parent = xJoint.Element("parent")?.Attribute("link")?.Value ?? string.Empty;
                string child = xJoint.Element("child")?.Attribute("link")?.Value ?? string.Empty;

                double[] xyz = { 0, 0, 0 };
                double[] rpy = { 0, 0, 0 };
                var xOrigin = xJoint.Element("origin");
                if (xOrigin != null)
                {
                    string? xyzStr = xOrigin.Attribute("xyz")?.Value;
                    if (!string.IsNullOrEmpty(xyzStr))
                    {
                        var tokens = xyzStr.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (tokens.Length >= 3)
                        {
                            double.TryParse(tokens[0], out xyz[0]);
                            double.TryParse(tokens[1], out xyz[1]);
                            double.TryParse(tokens[2], out xyz[2]);
                        }
                    }

                    string? rpyStr = xOrigin.Attribute("rpy")?.Value;
                    if (!string.IsNullOrEmpty(rpyStr))
                    {
                        var tokens = rpyStr.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (tokens.Length >= 3)
                        {
                            double.TryParse(tokens[0], out rpy[0]);
                            double.TryParse(tokens[1], out rpy[1]);
                            double.TryParse(tokens[2], out rpy[2]);
                        }
                    }
                }

                double[] axis = { 0, 0, 1 };
                var xAxis = xJoint.Element("axis");
                if (xAxis != null)
                {
                    string? axisStr = xAxis.Attribute("xyz")?.Value;
                    if (!string.IsNullOrEmpty(axisStr))
                    {
                        var tokens = axisStr.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (tokens.Length >= 3)
                        {
                            double.TryParse(tokens[0], out axis[0]);
                            double.TryParse(tokens[1], out axis[1]);
                            double.TryParse(tokens[2], out axis[2]);
                        }
                    }
                }

                var limits = new JointLimits { Lower = -3.1415, Upper = 3.1415, Effort = 10.0, Velocity = 1.5 };
                var xLimit = xJoint.Element("limit");
                if (xLimit != null)
                {
                    double.TryParse(xLimit.Attribute("lower")?.Value, out double lower);
                    double.TryParse(xLimit.Attribute("upper")?.Value, out double upper);
                    double.TryParse(xLimit.Attribute("effort")?.Value, out double effort);
                    double.TryParse(xLimit.Attribute("velocity")?.Value, out double velocity);

                    limits = new JointLimits { Lower = lower, Upper = upper, Effort = effort, Velocity = velocity };
                }

                joints.Add(new RobotJoint
                {
                    Name = jointName,
                    Type = type,
                    Parent = parent,
                    Child = child,
                    Origin = new JointOrigin { Xyz = xyz, Rpy = rpy },
                    Axis = axis,
                    Limits = limits
                });
            }

            string rootLink = "base_link";
            if (links.Count > 0)
            {
                var children = new HashSet<string>(joints.Select(j => j.Child));
                var root = links.FirstOrDefault(l => !children.Contains(l.Name));
                if (root != null) rootLink = root.Name;
            }

            return new RobotConfig
            {
                RobotName = robotName,
                RootLink = rootLink,
                Links = links,
                Joints = joints
            };
        }

        public static RobotConfig ParseUsdaFile(string usdaPath)
        {
            var lines = File.ReadAllLines(usdaPath);
            string robotName = "ImportedRobot";
            var links = new List<RobotLink>();
            var joints = new List<RobotJoint>();

            foreach (var line in lines)
            {
                if (line.Contains("defaultPrim ="))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(line, "\"[^\"]+\"");
                    if (match.Success)
                    {
                        robotName = match.Value.Trim('"');
                        break;
                    }
                }
            }

            int idx = 0;
            while (idx < lines.Length)
            {
                string line = lines[idx].Trim();

                if (line.StartsWith("def Xform") && !line.Contains(robotName) && !line.Contains("visual_mesh"))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(line, "\"[^\"]+\"");
                    if (match.Success)
                    {
                        string linkName = match.Value.Trim('"');
                        double mass = 0.001;
                        double[] com = { 0, 0, 0 };
                        double[] diagI = { 1e-5, 1e-5, 1e-5 };
                        string meshPath = string.Empty;

                        idx++;
                        while (idx < lines.Length && !lines[idx].Trim().StartsWith("}"))
                        {
                            string subLine = lines[idx].Trim();
                            if (subLine.StartsWith("float physics:mass"))
                            {
                                double.TryParse(subLine.Split('=').Last().Trim(), out mass);
                            }
                            else if (subLine.StartsWith("point3f physics:centerOfMass"))
                            {
                                var numbers = System.Text.RegularExpressions.Regex.Matches(subLine, @"[-+]?[0-9]*\.?[0-9]+([eE][-+]?[0-9]+)?");
                                if (numbers.Count >= 3)
                                {
                                    double.TryParse(numbers[0].Value, out com[0]);
                                    double.TryParse(numbers[1].Value, out com[1]);
                                    double.TryParse(numbers[2].Value, out com[2]);
                                }
                            }
                            else if (subLine.StartsWith("vector3f physics:diagonalInertia"))
                            {
                                var numbers = System.Text.RegularExpressions.Regex.Matches(subLine, @"[-+]?[0-9]*\.?[0-9]+([eE][-+]?[0-9]+)?");
                                if (numbers.Count >= 3)
                                {
                                    double.TryParse(numbers[0].Value, out diagI[0]);
                                    double.TryParse(numbers[1].Value, out diagI[1]);
                                    double.TryParse(numbers[2].Value, out diagI[2]);
                                }
                            }
                            else if (subLine.Contains("prepend references"))
                            {
                                var refMatch = System.Text.RegularExpressions.Regex.Match(subLine, @"@\.\/([^@]+)@");
                                if (refMatch.Success)
                                {
                                    meshPath = refMatch.Groups[1].Value;
                                }
                            }
                            idx++;
                        }

                        links.Add(new RobotLink
                        {
                            Name = linkName,
                            MeshPath = meshPath,
                            Mass = mass,
                            CenterOfMass = com,
                            Inertia = new Models.Inertia { Ixx = diagI[0], Iyy = diagI[1], Izz = diagI[2] }
                        });
                    }
                }
                else if (line.StartsWith("def PhysicsRevoluteJoint") || line.StartsWith("def PhysicsFixedJoint") || line.StartsWith("def PhysicsPrismaticJoint"))
                {
                    string jType = "fixed";
                    if (line.Contains("PhysicsRevoluteJoint")) jType = "revolute";
                    else if (line.Contains("PhysicsPrismaticJoint")) jType = "prismatic";

                    var match = System.Text.RegularExpressions.Regex.Match(line, "\"[^\"]+\"");
                    if (match.Success)
                    {
                        string jointName = match.Value.Trim('"');
                        string parent = string.Empty;
                        string child = string.Empty;
                        double[] xyz = { 0, 0, 0 };
                        double[] rpy = { 0, 0, 0 };
                        double[] axis = { 0, 0, 1 };
                        double lower = -3.1415, upper = 3.1415;

                        idx++;
                        while (idx < lines.Length && !lines[idx].Trim().StartsWith("}"))
                        {
                            string subLine = lines[idx].Trim();
                            if (subLine.StartsWith("rel physics:body0"))
                            {
                                parent = subLine.Split('/').Last().Trim('>', ' ');
                            }
                            else if (subLine.StartsWith("rel physics:body1"))
                            {
                                child = subLine.Split('/').Last().Trim('>', ' ');
                            }
                            else if (subLine.StartsWith("point3f physics:localPos0"))
                            {
                                var numbers = System.Text.RegularExpressions.Regex.Matches(subLine, @"[-+]?[0-9]*\.?[0-9]+([eE][-+]?[0-9]+)?");
                                if (numbers.Count >= 3)
                                {
                                    double.TryParse(numbers[0].Value, out xyz[0]);
                                    double.TryParse(numbers[1].Value, out xyz[1]);
                                    double.TryParse(numbers[2].Value, out xyz[2]);
                                }
                            }
                            else if (subLine.StartsWith("quatf physics:localRot0"))
                            {
                                var numbers = System.Text.RegularExpressions.Regex.Matches(subLine, @"[-+]?[0-9]*\.?[0-9]+([eE][-+]?[0-9]+)?");
                                if (numbers.Count >= 4)
                                {
                                    double.TryParse(numbers[0].Value, out double qw);
                                    double.TryParse(numbers[1].Value, out double qx);
                                    double.TryParse(numbers[2].Value, out double qy);
                                    double.TryParse(numbers[3].Value, out double qz);
                                    rpy = QuaternionToRpy(qw, qx, qy, qz);
                                }
                            }
                            else if (subLine.StartsWith("token physics:axis"))
                            {
                                string axisTok = subLine.Split('=').Last().Trim('"', ' ', ';');
                                if (axisTok == "X") axis = new double[] { 1, 0, 0 };
                                else if (axisTok == "Y") axis = new double[] { 0, 1, 0 };
                                else axis = new double[] { 0, 0, 1 };
                            }
                            else if (subLine.StartsWith("float physics:lowerLimit"))
                            {
                                double.TryParse(subLine.Split('=').Last().Trim(';', ' '), out double val);
                                lower = (jType == "revolute") ? val * Math.PI / 180.0 : val;
                            }
                            else if (subLine.StartsWith("float physics:upperLimit"))
                            {
                                double.TryParse(subLine.Split('=').Last().Trim(';', ' '), out double val);
                                upper = (jType == "revolute") ? val * Math.PI / 180.0 : val;
                            }
                            idx++;
                        }

                        joints.Add(new RobotJoint
                        {
                            Name = jointName,
                            Type = jType,
                            Parent = parent,
                            Child = child,
                            Origin = new JointOrigin { Xyz = xyz, Rpy = rpy },
                            Axis = axis,
                            Limits = new JointLimits { Lower = lower, Upper = upper, Effort = 10.0, Velocity = 1.5 }
                        });
                    }
                }
                idx++;
            }

            string rootLink = "base_link";
            if (links.Count > 0)
            {
                var children = new HashSet<string>(joints.Select(j => j.Child));
                var root = links.FirstOrDefault(l => !children.Contains(l.Name));
                if (root != null) rootLink = root.Name;
            }

            return new RobotConfig
            {
                RobotName = robotName,
                RootLink = rootLink,
                Links = links,
                Joints = joints
            };
        }

        private static double[] QuaternionToRpy(double w, double x, double y, double z)
        {
            double sinr_cosp = 2 * (w * x + y * z);
            double cosr_cosp = 1 - 2 * (x * x + y * y);
            double roll = Math.Atan2(sinr_cosp, cosr_cosp);

            double sinp = 2 * (w * y - z * x);
            double pitch;
            if (Math.Abs(sinp) >= 1)
                pitch = Math.CopySign(Math.PI / 2, sinp);
            else
                pitch = Math.Asin(sinp);

            double siny_cosp = 2 * (w * z + x * y);
            double cosy_cosp = 1 - 2 * (y * y + z * z);
            double yaw = Math.Atan2(siny_cosp, cosy_cosp);

            return new double[] { roll, pitch, yaw };
        }
    }
}
