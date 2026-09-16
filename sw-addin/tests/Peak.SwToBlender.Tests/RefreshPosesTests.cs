using Peak.SwToBlender;
using Peak.SwToBlender.Core;
using System.Collections.Generic;
using Xunit;

namespace Peak.SwToBlender.Tests
{
    /// <summary>
    /// What Refresh Poses tells the user, from what Blender answered.
    ///
    /// The payload itself needs SolidWorks, so it is checked live. What is
    /// checked here is the part a user reads, because a refresh that moved
    /// nothing looks exactly like one that failed unless the message says
    /// which it was.
    /// </summary>
    public class RefreshPosesTests
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
                RefreshPosesCommand.Summary(Reply(true, 3), 4));
        }

        [Fact]
        public void NothingToMoveIsSaidPlainly()
        {
            Assert.Equal("Sent 4 component pose(s). Every part in Blender was "
                       + "already where SolidWorks has it.",
                RefreshPosesCommand.Summary(Reply(true, 0), 4));
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
                RefreshPosesCommand.Summary(refused, 4));
        }

        [Fact]
        public void ARefusalWithNoReasonStillSaysSomething()
        {
            var refused = new Dictionary<string, object> { { "ok", false } };
            Assert.Equal("Blender refused the poses.",
                RefreshPosesCommand.Summary(refused, 4));
        }

        [Fact]
        public void NoAnswerIsNotASuccess()
        {
            Assert.Equal("Blender did not answer.",
                RefreshPosesCommand.Summary(null, 4));
        }
    }
}
