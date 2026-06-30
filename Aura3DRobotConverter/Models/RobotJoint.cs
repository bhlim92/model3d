using System.Text.Json.Serialization;

namespace Aura3DRobotConverter.Models
{
    public class JointOrigin
    {
        [JsonPropertyName("xyz")]
        public double[] Xyz { get; set; } = new double[3] { 0, 0, 0 };

        [JsonPropertyName("rpy")]
        public double[] Rpy { get; set; } = new double[3] { 0, 0, 0 };
    }

    public class JointLimits
    {
        [JsonPropertyName("lower")]
        public double Lower { get; set; } = -3.14159;

        [JsonPropertyName("upper")]
        public double Upper { get; set; } = 3.14159;

        [JsonPropertyName("effort")]
        public double Effort { get; set; } = 10.0;

        [JsonPropertyName("velocity")]
        public double Velocity { get; set; } = 1.5;
    }

    public class RobotJoint
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = "revolute";

        [JsonPropertyName("parent")]
        public string Parent { get; set; } = string.Empty;

        [JsonPropertyName("child")]
        public string Child { get; set; } = string.Empty;

        [JsonPropertyName("origin")]
        public JointOrigin Origin { get; set; } = new JointOrigin();

        [JsonPropertyName("axis")]
        public double[] Axis { get; set; } = new double[3] { 0, 0, 1 };

        [JsonPropertyName("limits")]
        public JointLimits Limits { get; set; } = new JointLimits();
    }
}
