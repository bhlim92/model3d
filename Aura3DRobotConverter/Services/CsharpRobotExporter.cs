using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Aura3DRobotConverter.Models;

namespace Aura3DRobotConverter.Services
{
    public class CsharpRobotExporter
    {
        public static string GetUrdfText(RobotConfig config)
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                BuildUrdf(config, tempFile);
                return File.ReadAllText(tempFile);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        public static string ExportRobotModel(RobotConfig config, string sessionDir)
        {
            string robotName = config.RobotName ?? "AuraRobot";

            // 1. Save local assembly structure config JSON
            string jsonPath = Path.Combine(sessionDir, "assembly_structure.json");
            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            string jsonString = JsonSerializer.Serialize(config, jsonOptions);
            File.WriteAllText(jsonPath, jsonString, Encoding.UTF8);

            // 2. Build URDF XML
            string urdfFilename = $"{robotName}.urdf";
            string urdfPath = Path.Combine(sessionDir, urdfFilename);
            BuildUrdf(config, urdfPath);

            // 3. Build USDA (Pixar USD with Physics)
            string usdFilename = $"{robotName}.usda";
            string usdPath = Path.Combine(sessionDir, usdFilename);
            BuildUsd(config, usdPath);

            // 4. Create ZIP package using native ZipFile
            string parentDir = Path.GetDirectoryName(sessionDir) ?? sessionDir;
            string zipFilename = $"{robotName}_urdf_package.zip";
            string zipPath = Path.Combine(parentDir, zipFilename);

            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            ZipFile.CreateFromDirectory(sessionDir, zipPath, CompressionLevel.Optimal, false);

            return zipFilename;
        }

        private static void BuildUrdf(RobotConfig config, string outputPath)
        {
            string robotName = config.RobotName ?? "AuraRobot";

            var xRoot = new XElement("robot", new XAttribute("name", robotName));

            // Write Links
            foreach (var link in config.Links)
            {
                var xLink = new XElement("link", new XAttribute("name", link.Name));

                // Inertial attributes
                var xInertial = new XElement("inertial");
                xInertial.Add(new XElement("mass", new XAttribute("value", link.Mass.ToString("F6"))));
                
                var com = link.CenterOfMass;
                xInertial.Add(new XElement("origin", 
                    new XAttribute("xyz", $"{com[0]:F6} {com[1]:F6} {com[2]:F6}"), 
                    new XAttribute("rpy", "0.000000 0.000000 0.000000")));

                var inertia = link.Inertia;
                xInertial.Add(new XElement("inertia",
                    new XAttribute("ixx", inertia.Ixx.ToString("E6")),
                    new XAttribute("ixy", inertia.Ixy.ToString("E6")),
                    new XAttribute("ixz", inertia.Ixz.ToString("E6")),
                    new XAttribute("iyy", inertia.Iyy.ToString("E6")),
                    new XAttribute("iyz", inertia.Iyz.ToString("E6")),
                    new XAttribute("izz", inertia.Izz.ToString("E6"))));

                xLink.Add(xInertial);

                // Add visual geometry elements if STL meshes exist
                if (!string.IsNullOrEmpty(link.MeshPath))
                {
                    var xVisual = new XElement("visual");
                    xVisual.Add(new XElement("origin", new XAttribute("xyz", "0 0 0"), new XAttribute("rpy", "0 0 0")));
                    xVisual.Add(new XElement("geometry", 
                        new XElement("mesh", new XAttribute("filename", $"package://{robotName}/{link.MeshPath}"))));
                    xLink.Add(xVisual);

                    var xCollision = new XElement("collision");
                    xCollision.Add(new XElement("origin", new XAttribute("xyz", "0 0 0"), new XAttribute("rpy", "0 0 0")));
                    xCollision.Add(new XElement("geometry", 
                        new XElement("mesh", new XAttribute("filename", $"package://{robotName}/{link.MeshPath}"))));
                    xLink.Add(xCollision);
                }

                xRoot.Add(xLink);
            }

            // Write Joints
            foreach (var joint in config.Joints)
            {
                var xJoint = new XElement("joint", 
                    new XAttribute("name", joint.Name), 
                    new XAttribute("type", joint.Type));

                xJoint.Add(new XElement("parent", new XAttribute("link", joint.Parent)));
                xJoint.Add(new XElement("child", new XAttribute("link", joint.Child)));

                var origin = joint.Origin;
                xJoint.Add(new XElement("origin", 
                    new XAttribute("xyz", $"{origin.Xyz[0]:F6} {origin.Xyz[1]:F6} {origin.Xyz[2]:F6}"),
                    new XAttribute("rpy", $"{origin.Rpy[0]:F6} {origin.Rpy[1]:F6} {origin.Rpy[2]:F6}")));

                xJoint.Add(new XElement("axis", new XAttribute("xyz", $"{joint.Axis[0]:F1} {joint.Axis[1]:F1} {joint.Axis[2]:F1}")));

                if (joint.Type == "revolute" || joint.Type == "prismatic")
                {
                    var limits = joint.Limits;
                    xJoint.Add(new XElement("limit",
                        new XAttribute("lower", limits.Lower.ToString("F4")),
                        new XAttribute("upper", limits.Upper.ToString("F4")),
                        new XAttribute("effort", limits.Effort.ToString("F1")),
                        new XAttribute("velocity", limits.Velocity.ToString("F1"))));
                }

                xRoot.Add(xJoint);
            }

            var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), xRoot);
            doc.Save(outputPath);
        }

        private static string SanitizeUsdName(string name)
        {
            string sanitized = System.Text.RegularExpressions.Regex.Replace(name, @"[^a-zA-Z0-9_]", "_");
            if (sanitized.Length > 0 && char.IsDigit(sanitized[0]))
            {
                sanitized = "r_" + sanitized;
            }
            return sanitized;
        }

        private static void BuildUsd(RobotConfig config, string outputPath)
        {
            string robotName = SanitizeUsdName(config.RobotName ?? "AuraRobot");

            var sb = new StringBuilder();
            sb.AppendLine("#usda 1.0");
            sb.AppendLine("(");
            sb.AppendLine("    defaultPrim = \"" + robotName + "\"");
            sb.AppendLine("    upAxis = \"Z\"");
            sb.AppendLine(")");
            sb.AppendLine();
            sb.AppendLine($"def Xform \"{robotName}\"");
            sb.AppendLine("(");
            sb.AppendLine("    apiSchemas = [\"PhysicsArticulationRootAPI\"]");
            sb.AppendLine(")");
            sb.AppendLine("{");

            // 1. Write Xform links
            foreach (var link in config.Links)
            {
                string linkName = SanitizeUsdName(link.Name);
                sb.AppendLine($"    def Xform \"{linkName}\"");
                sb.AppendLine("    (");
                sb.AppendLine("        apiSchemas = [\"PhysicsMassAPI\"]");
                sb.AppendLine("    )");
                sb.AppendLine("    {");
                sb.AppendLine($"        float physics:mass = {link.Mass:F6}");
                
                var com = link.CenterOfMass;
                sb.AppendLine($"        point3f physics:centerOfMass = ({com[0]:F6}, {com[1]:F6}, {com[2]:F6})");
                
                var inertia = link.Inertia;
                sb.AppendLine($"        vector3f physics:diagonalInertia = ({inertia.Ixx:E6}, {inertia.Iyy:E6}, {inertia.Izz:E6})");

                if (!string.IsNullOrEmpty(link.MeshPath))
                {
                    sb.AppendLine("        def Xform \"visual_mesh\"");
                    sb.AppendLine("        (");
                    sb.AppendLine("            apiSchemas = [\"PhysicsCollisionAPI\"]");
                    sb.AppendLine("            prepend references = @./" + link.MeshPath + "@");
                    sb.AppendLine("        )");
                    sb.AppendLine("        {");
                    sb.AppendLine("        }");
                }

                sb.AppendLine("    }");
                sb.AppendLine();
            }

            // 2. Write Physics Joints
            foreach (var joint in config.Joints)
            {
                string jointName = SanitizeUsdName(joint.Name);
                string parentName = SanitizeUsdName(joint.Parent);
                string childName = SanitizeUsdName(joint.Child);
                string jointType = joint.Type;

                string usdJointType = "PhysicsFixedJoint";
                if (jointType == "revolute" || jointType == "continuous") usdJointType = "PhysicsRevoluteJoint";
                else if (jointType == "prismatic") usdJointType = "PhysicsPrismaticJoint";

                sb.AppendLine($"    def {usdJointType} \"{jointName}\"");
                sb.AppendLine("    {");
                sb.AppendLine($"        rel physics:body0 = </{robotName}/{parentName}>");
                sb.AppendLine($"        rel physics:body1 = </{robotName}/{childName}>");

                var origin = joint.Origin;
                sb.AppendLine($"        point3f physics:localPos0 = ({origin.Xyz[0]:F6}, {origin.Xyz[1]:F6}, {origin.Xyz[2]:F6})");
                
                // Simple quaternion derivation from roll angle offset (X rotation axis mapping)
                double rollDeg = origin.Rpy[0] * 180.0 / Math.PI;
                double halfAngleRad = (rollDeg * Math.PI / 180.0) / 2.0;
                double qReal = Math.Cos(halfAngleRad);
                double qImg = Math.Sin(halfAngleRad);
                sb.AppendLine($"        quatf physics:localRot0 = ({qReal:F6}, {qImg:F6}, 0.000000, 0.000000)");

                sb.AppendLine("        point3f physics:localPos1 = (0.000000, 0.000000, 0.000000)");
                sb.AppendLine("        quatf physics:localRot1 = (1.000000, 0.000000, 0.000000, 0.000000)");

                if (jointType == "revolute" || jointType == "continuous" || jointType == "prismatic")
                {
                    // Map axis token
                    var axis = joint.Axis;
                    string axisToken = "Z";
                    if (Math.Abs(axis[0]) > 0.9) axisToken = "X";
                    else if (Math.Abs(axis[1]) > 0.9) axisToken = "Y";
                    sb.AppendLine($"        token physics:axis = \"{axisToken}\"");

                    if (jointType == "revolute")
                    {
                        double lowerDeg = joint.Limits.Lower * 180.0 / Math.PI;
                        double upperDeg = joint.Limits.Upper * 180.0 / Math.PI;
                        sb.AppendLine($"        float physics:lowerLimit = {lowerDeg:F4}");
                        sb.AppendLine($"        float physics:upperLimit = {upperDeg:F4}");
                    }
                    else if (jointType == "prismatic")
                    {
                        sb.AppendLine($"        float physics:lowerLimit = {joint.Limits.Lower:F4}");
                        sb.AppendLine($"        float physics:upperLimit = {joint.Limits.Upper:F4}");
                    }
                }

                sb.AppendLine("    }");
                sb.AppendLine();
            }

            sb.AppendLine("}");

            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
        }
    }
}
