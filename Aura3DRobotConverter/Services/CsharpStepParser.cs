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
        public static RobotConfig ParseStepFile(string stepFilePath, string outputDir, double density = 2700.0, string upAxis = "Z")
        {
            string cacheJsonPath = System.IO.Path.ChangeExtension(stepFilePath, ".json");
            if (System.IO.File.Exists(cacheJsonPath))
            {
                try
                {
                    string json = System.IO.File.ReadAllText(cacheJsonPath);
                    var cachedConfig = System.Text.Json.JsonSerializer.Deserialize<RobotConfig>(json);
                    if (cachedConfig != null)
                    {
                        if (cachedConfig.UpAxis == upAxis)
                        {
                            bool allMeshesValid = true;
                            foreach (var link in cachedConfig.Links)
                            {
                                if (!string.IsNullOrEmpty(link.MeshPath))
                                {
                                    // Remove meshes/ prefix or search in meshes subfolder
                                    string fullMeshPath = System.IO.Path.Combine(outputDir, link.MeshPath);
                                    if (!System.IO.File.Exists(fullMeshPath) || new System.IO.FileInfo(fullMeshPath).Length < 100)
                                    {
                                        allMeshesValid = false;
                                        break;
                                    }
                                }
                            }
                            if (allMeshesValid)
                            {
                                return cachedConfig;
                            }
                        }
                    }
                }
                catch (Exception) { /* Fallback to parsing */ }
            }

            string meshDir = System.IO.Path.Combine(outputDir, "meshes");
            if (System.IO.Directory.Exists(meshDir))
            {
                try
                {
                    foreach (var file in System.IO.Directory.GetFiles(meshDir))
                    {
                        System.IO.File.Delete(file);
                    }
                }
                catch (Exception) { }
            }
            Directory.CreateDirectory(meshDir);

            // Initialize AnyCAD Shape Reader
            TopoShape shape = ShapeIO.Open(stepFilePath);
            if (shape == null)
            {
                throw new Exception("AnyCAD engine failed to parse and load the STEP file.");
            }

            // Apply rotation based on UpAxis specification to make Z-up
            if (upAxis == "Y")
            {
                // Rotate around X by 90 degrees to map Y to Z (per user instruction)
                GTrsf trsf = new GTrsf();
                trsf.SetRotation(new GAx1(new GPnt(0, 0, 0), new GDir(1, 0, 0)), Math.PI / 2.0);
                shape = TransformTool.Transform(shape, trsf);
            }

            var links = new List<RobotLink>();
            var joints = new List<RobotJoint>();

            // 1. Explore all shells recursively using native TopoExplor (blazing fast ~10ms)
            var shellExplor = new TopoExplor(shape, EnumTopoShapeType.Topo_SHELL, EnumTopoShapeType.Topo_SHAPE);
            var solidsList = shellExplor.GetChildrenShapes();
            var solids = new List<TopoShape>();
            for (int i = 0; i < solidsList.Count; i++)
            {
                solids.Add(solidsList[i]);
            }

            // 2. Calculate physical properties using Bounding Box analytical estimation (blazing fast ~1ms total)
            for (int i = 0; i < solids.Count; i++)
            {
                var subShape = solids[i];
                string linkName = (i == 0) ? "base_link" : $"link_{i}";
                string meshFilename = $"{linkName}.stl";

                var bbox = subShape.GetBBox();
                var min = bbox.CornerMin();
                var max = bbox.CornerMax();

                double dx = (max.X() - min.X()) / 1000.0; // mm -> m
                double dy = (max.Y() - min.Y()) / 1000.0;
                double dz = (max.Z() - min.Z()) / 1000.0;

                dx = Math.Max(dx, 0.001);
                dy = Math.Max(dy, 0.001);
                dz = Math.Max(dz, 0.001);

                double cx = (min.X() + max.X()) / 2.0;
                double cy = (min.Y() + max.Y()) / 2.0;
                double cz = (min.Z() + max.Z()) / 2.0;
                double[] com = new double[] { cx / 1000.0, cy / 1000.0, cz / 1000.0 }; // mm -> m

                double volumeM3 = dx * dy * dz * 0.25;
                double mass = Math.Max(volumeM3 * density, 0.001); // mass in kg

                // Analytical moments of inertia for rectangular cuboid
                double ixx = (1.0 / 12.0) * mass * (dy * dy + dz * dz);
                double iyy = (1.0 / 12.0) * mass * (dx * dx + dz * dz);
                double izz = (1.0 / 12.0) * mass * (dx * dx + dy * dy);

                double ixy = 0.0;
                double ixz = 0.0;
                double iyz = 0.0;

                links.Add(new RobotLink
                {
                    Name = linkName,
                    MeshPath = $"meshes/{meshFilename}",
                    Mass = mass,
                    CenterOfMass = com,
                    Inertia = new Models.Inertia
                    {
                        Ixx = ixx, Iyy = iyy, Izz = izz,
                        Ixy = ixy, Ixz = ixz, Iyz = iyz
                    }
                });

                // Generate STL cache
                string absoluteMeshPath = System.IO.Path.Combine(outputDir, $"meshes/{meshFilename}");
                if (!System.IO.File.Exists(absoluteMeshPath))
                {
                    if (System.Windows.Application.Current != null)
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            var tempNode = AnyCAD.Foundation.BrepSceneNode.Create(subShape);
                            tempNode?.Dispose();
                        });
                    }
                    AnyCAD.Foundation.ShapeIO.Save(subShape, absoluteMeshPath);
                }
            }

            if (links.Count == 0)
            {
                throw new Exception("No valid rigid 3D solids detected inside the STEP file.");
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

            var config = new RobotConfig
            {
                RobotName = System.IO.Path.GetFileNameWithoutExtension(stepFilePath),
                RootLink = "base_link",
                Links = links,
                Joints = joints,
                UpAxis = upAxis,
                OcpSolids = solids
            };

            // Save JSON Cache
            try
            {
                var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                string jsonOutput = System.Text.Json.JsonSerializer.Serialize(config, options);
                System.IO.File.WriteAllText(cacheJsonPath, jsonOutput);
            }
            catch (Exception) { }

            return config;
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
                    double.TryParse(xInertial.Element("mass")?.Attribute("value")?.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out mass);
                    
                    string? xyzStr = xInertial.Element("origin")?.Attribute("xyz")?.Value;
                    if (!string.IsNullOrEmpty(xyzStr))
                    {
                        var tokens = xyzStr.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (tokens.Length >= 3)
                        {
                            double.TryParse(tokens[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out com[0]);
                            double.TryParse(tokens[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out com[1]);
                            double.TryParse(tokens[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out com[2]);
                        }
                    }

                    var xInertia = xInertial.Element("inertia");
                    if (xInertia != null)
                    {
                        double.TryParse(xInertia.Attribute("ixx")?.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ixx);
                        double.TryParse(xInertia.Attribute("ixy")?.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ixy);
                        double.TryParse(xInertia.Attribute("ixz")?.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ixz);
                        double.TryParse(xInertia.Attribute("iyy")?.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double iyy);
                        double.TryParse(xInertia.Attribute("iyz")?.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double iyz);
                        double.TryParse(xInertia.Attribute("izz")?.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double izz);

                        inertia = new Models.Inertia
                        {
                            Ixx = ixx, Ixy = ixy, Ixz = ixz,
                            Iyy = iyy, Iyz = iyz, Izz = izz
                        };
                    }
                }

                string meshPath = string.Empty;
                string? primitiveType = null;
                double[]? primitiveParams = null;
                float[]? colorRgba = null;

                var xVisual = xLink.Element("visual");
                if (xVisual != null)
                {
                    var xGeom = xVisual.Element("geometry");
                    if (xGeom != null)
                    {
                        var xMesh = xGeom.Element("mesh");
                        if (xMesh != null)
                        {
                            string filename = xMesh.Attribute("filename")?.Value ?? string.Empty;
                            if (filename.StartsWith("package://"))
                            {
                                int slashIndex = filename.IndexOf('/', 10);
                                if (slashIndex != -1) meshPath = filename.Substring(slashIndex + 1);
                            }
                            else
                            {
                                meshPath = filename;
                            }
                        }
                        else if (xGeom.Element("box") != null)
                        {
                            primitiveType = "box";
                            var sizeStr = xGeom.Element("box")?.Attribute("size")?.Value;
                            if (sizeStr != null) {
                                var tokens = sizeStr.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                                if (tokens.Length >= 3) {
                                    primitiveParams = new double[3];
                                    double.TryParse(tokens[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out primitiveParams[0]);
                                    double.TryParse(tokens[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out primitiveParams[1]);
                                    double.TryParse(tokens[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out primitiveParams[2]);
                                }
                            }
                        }
                        else if (xGeom.Element("cylinder") != null)
                        {
                            primitiveType = "cylinder";
                            var cyl = xGeom.Element("cylinder");
                            double radius = 0, length = 0;
                            double.TryParse(cyl?.Attribute("radius")?.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out radius);
                            double.TryParse(cyl?.Attribute("length")?.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out length);
                            primitiveParams = new double[] { radius, length };
                        }
                        else if (xGeom.Element("sphere") != null)
                        {
                            primitiveType = "sphere";
                            double radius = 0;
                            double.TryParse(xGeom.Element("sphere")?.Attribute("radius")?.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out radius);
                            primitiveParams = new double[] { radius };
                        }
                    }

                    var xMat = xVisual.Element("material");
                    if (xMat != null)
                    {
                        var xColor = xMat.Element("color");
                        if (xColor != null)
                        {
                            string? rgbaStr = xColor.Attribute("rgba")?.Value;
                            if (rgbaStr != null) {
                                var tokens = rgbaStr.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                                if (tokens.Length >= 4) {
                                    colorRgba = new float[4];
                                    float.TryParse(tokens[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out colorRgba[0]);
                                    float.TryParse(tokens[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out colorRgba[1]);
                                    float.TryParse(tokens[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out colorRgba[2]);
                                    float.TryParse(tokens[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out colorRgba[3]);
                                }
                            }
                        }
                    }
                }

                    var xVisualOrigin = xVisual?.Element("origin");
                    double[] visualXyz = { 0, 0, 0 };
                    double[] visualRpy = { 0, 0, 0 };
                    if (xVisualOrigin != null)
                    {
                        var xyzStr2 = xVisualOrigin.Attribute("xyz")?.Value;
                        if (!string.IsNullOrEmpty(xyzStr2)) {
                            var t = xyzStr2.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            if (t.Length >= 3) {
                                double.TryParse(t[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out visualXyz[0]);
                                double.TryParse(t[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out visualXyz[1]);
                                double.TryParse(t[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out visualXyz[2]);
                            }
                        }
                        var rpyStr2 = xVisualOrigin.Attribute("rpy")?.Value;
                        if (!string.IsNullOrEmpty(rpyStr2)) {
                            var t = rpyStr2.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            if (t.Length >= 3) {
                                double.TryParse(t[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out visualRpy[0]);
                                double.TryParse(t[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out visualRpy[1]);
                                double.TryParse(t[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out visualRpy[2]);
                            }
                        }
                    }

                links.Add(new RobotLink
                {
                    Name = linkName,
                    MeshPath = meshPath,
                    Mass = mass,
                    CenterOfMass = com,
                    Inertia = inertia,
                    PrimitiveType = primitiveType,
                    PrimitiveParams = primitiveParams,
                    ColorRgba = colorRgba,
                    VisualOriginXyz = visualXyz,
                    VisualOriginRpy = visualRpy
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

                        int depth = 0;
                        bool opened = false;
                        idx++;
                        while (idx < lines.Length)
                        {
                            string subLine = lines[idx].Trim();
                            
                            if (subLine.Contains("{")) { depth++; opened = true; }
                            if (subLine.Contains("}")) depth--;

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

                            if (opened && depth <= 0) break;
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

                        int depth = 0;
                        bool opened = false;
                        idx++;
                        while (idx < lines.Length)
                        {
                            string subLine = lines[idx].Trim();

                            if (subLine.Contains("{")) { depth++; opened = true; }
                            if (subLine.Contains("}")) depth--;

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

                            if (opened && depth <= 0) break;
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
