using System.Collections.Generic;
using Peak.Cadder.Bridge;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// What a pose push tells the user. The payload itself needs
    /// SolidWorks, so it is checked live; this is the part a user reads,
    /// because a push that moved nothing looks exactly like one that failed
    /// unless the message says which it was.
    /// </summary>
    public class PosePushTests
    {
        private static Dictionary<string, object> Reply(bool ok, int moved)
        {
            return new Dictionary<string, object>
            {
                { "ok", ok },
                {
                    "stages", new Dictionary<string, object>
                    {
                        {
                            "poses", new Dictionary<string, object>
                            {
                                { "moved", (double)moved }, { "components", 4.0 },
                            }
                        },
                    }
                },
            };
        }

        [Fact]
        public void MovedPartsAreCounted()
        {
            Assert.Equal("Sent 4 component pose(s). Blender moved 3 part(s).",
                PosePush.Summary(Reply(true, 3), 4));
        }

        [Fact]
        public void NothingToMoveIsSaidPlainly()
        {
            Assert.Equal("Sent 4 component pose(s). Every part in Blender was "
                       + "already where SolidWorks has it.",
                PosePush.Summary(Reply(true, 0), 4));
        }

        [Fact]
        public void BlendersOwnReasonIsPassedOn()
        {
            var refused = new Dictionary<string, object>
            {
                { "ok", false },
                { "error", "Send the assembly to Blender first: this scene "
                           + "has no manifest to update." },
            };
            Assert.Equal("Send the assembly to Blender first: this scene has "
                       + "no manifest to update.",
                PosePush.Summary(refused, 4));
        }

        [Fact]
        public void ARefusalWithNoReasonStillSaysSomething()
        {
            var refused = new Dictionary<string, object> { { "ok", false } };
            Assert.Equal("Blender refused the poses.",
                PosePush.Summary(refused, 4));
        }

        [Fact]
        public void NoAnswerAtAllIsNotAnEmptyMessage()
        {
            Assert.Equal("Blender did not answer.", PosePush.Summary(null, 4));
        }
    }
}
