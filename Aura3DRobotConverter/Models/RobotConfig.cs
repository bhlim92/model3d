using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Aura3DRobotConverter.Models
{
    public class RobotConfig
    {
        [JsonPropertyName("robot_name")]
        public string RobotName { get; set; } = "AuraRobot";

        [JsonPropertyName("root_link")]
        public string RootLink { get; set; } = "base_link";

        [JsonPropertyName("links")]
        public List<RobotLink> Links { get; set; } = new List<RobotLink>();

        [JsonPropertyName("joints")]
        public List<RobotJoint> Joints { get; set; } = new List<RobotJoint>();
    }
}
