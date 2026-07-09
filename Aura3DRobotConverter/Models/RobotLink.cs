using System.Text.Json.Serialization;

namespace Aura3DRobotConverter.Models
{
    public class Inertia
    {
        [JsonPropertyName("ixx")]
        public double Ixx { get; set; } = 1e-5;

        [JsonPropertyName("iyy")]
        public double Iyy { get; set; } = 1e-5;

        [JsonPropertyName("izz")]
        public double Izz { get; set; } = 1e-5;

        [JsonPropertyName("ixy")]
        public double Ixy { get; set; } = 0.0;

        [JsonPropertyName("ixz")]
        public double Ixz { get; set; } = 0.0;

        [JsonPropertyName("iyz")]
        public double Iyz { get; set; } = 0.0;
    }

    public class RobotLink
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("mesh_path")]
        public string MeshPath { get; set; } = string.Empty;

        [JsonPropertyName("mass")]
        public double Mass { get; set; } = 0.0;

        [JsonPropertyName("center_of_mass")]
        public double[] CenterOfMass { get; set; } = new double[3] { 0, 0, 0 };

        [JsonPropertyName("inertia")]
        public Inertia Inertia { get; set; } = new Inertia();

        [JsonPropertyName("primitive_type")]
        public string? PrimitiveType { get; set; }

        [JsonPropertyName("primitive_params")]
        public double[]? PrimitiveParams { get; set; }

        [JsonPropertyName("color_rgba")]
        public float[]? ColorRgba { get; set; }

        // Visual element's own <origin xyz rpy> (local offset of the geometry within the link)
        [JsonPropertyName("visual_origin_xyz")]
        public double[] VisualOriginXyz { get; set; } = new double[3] { 0, 0, 0 };

        [JsonPropertyName("visual_origin_rpy")]
        public double[] VisualOriginRpy { get; set; } = new double[3] { 0, 0, 0 };
    }
}
