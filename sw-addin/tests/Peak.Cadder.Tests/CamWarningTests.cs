using System.Collections.Generic;
using Peak.Cadder.Core.Model;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The "not rigged" warning of a cam mate goes when the export rigs
    /// that mate after all, and only then. The warning was matched to the
    /// mate by a part of its text, so with SolidWorks' default names the
    /// rigged CamMateTangent1 took the warnings of CamMateTangent10 to 19
    /// with it, and nobody heard about followers that do not move.
    /// </summary>
    public class CamWarningTests
    {
        private static ManifestWarning NotRigged(string mate)
        {
            return new ManifestWarning
            {
                Code = "CAM_FOLLOWER",
                Message = "Cam-follower mate " + mate + " is not rigged: the cam relation was "
                    + "not read off the model. The follower's own joint is exported; pose it "
                    + "by hand to match the cam.",
            };
        }

        [Fact]
        public void ARiggedMateTakesOnlyItsOwnWarning()
        {
            var warnings = new List<ManifestWarning>
            {
                NotRigged("CamMateTangent1"),
                NotRigged("CamMateTangent10"),
                NotRigged("CamMateTangent12"),
            };
            ExportCommand.DropRiggedCamWarnings(warnings, new List<string> { "CamMateTangent1" });
            Assert.Equal(2, warnings.Count);
            Assert.Contains("CamMateTangent10", warnings[0].Message);
            Assert.Contains("CamMateTangent12", warnings[1].Message);
        }

        [Fact]
        public void AMateNamedLikeTheTextTakesNothingElse()
        {
            var warnings = new List<ManifestWarning> { NotRigged("CamMateTangent4") };
            ExportCommand.DropRiggedCamWarnings(warnings, new List<string> { "follower" });
            Assert.Single(warnings);
        }

        [Fact]
        public void TheWarningWithTheReasonStillGoes()
        {
            // The export rewrites the message with the reason it could not
            // read the cam, and keeps the mate name in the same place.
            var warnings = new List<ManifestWarning>
            {
                new ManifestWarning
                {
                    Code = "CAM_FOLLOWER",
                    Message = "Cam-follower mate CamMateTangent3 is not rigged: the cam is free "
                        + "in its plane. The follower's own joint is exported; pose it by hand "
                        + "to match the cam.",
                },
                new ManifestWarning { Code = "UNDER_DEFINED", Message = "mate CamMateTangent3 " },
            };
            ExportCommand.DropRiggedCamWarnings(warnings, new List<string> { "CamMateTangent3" });
            Assert.Single(warnings);
            Assert.Equal("UNDER_DEFINED", warnings[0].Code);
        }
    }
}
