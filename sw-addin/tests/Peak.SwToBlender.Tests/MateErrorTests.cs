using System.Collections.Generic;
using Peak.SwToBlender;
using Xunit;

namespace Peak.SwToBlender.Tests
{
    /// <summary>
    /// What the user is told when an assembly has mate errors. SolidWorks
    /// decides for itself which mate of an over-defined set to ignore, so
    /// the kinematics cannot be trusted; the export offers the geometry
    /// without a rig rather than refusing outright (Oscar, 2026-09-15).
    /// </summary>
    public class MateErrorTests
    {
        private static List<string> Errors(int n)
        {
            var list = new List<string>();
            for (int i = 1; i <= n; i++) list.Add("Coincident" + i + " is over defining");
            return list;
        }

        [Fact]
        public void TheReportNamesTheMatesAndCountsTheRest()
        {
            string few = ExportCommand.MateErrorReport(Errors(2));
            Assert.Contains("Mate errors or over-defined mates", few);
            Assert.Contains("Coincident1 is over defining", few);
            Assert.Contains("Coincident2 is over defining", few);
            Assert.DoesNotContain("more", few);

            string many = ExportCommand.MateErrorReport(Errors(14));
            Assert.Contains("Coincident10 is over defining", many);
            Assert.DoesNotContain("Coincident11", many);
            Assert.Contains("and 4 more", many);
        }

        [Fact]
        public void TheQuestionOffersGeometryWithoutARigOrStopping()
        {
            string question = ExportCommand.MateErrorQuestion(ExportCommand.MateErrorReport(Errors(1)));
            Assert.Contains("Coincident1 is over defining", question);
            Assert.Contains("export the geometry without a rig?", question);
            Assert.Contains("Yes: export the geometry only, with no rig.", question);
            Assert.Contains("No: stop the export, so you can fix the mates.", question);
            // No em dash in what the user reads.
            Assert.DoesNotContain("\u2014", question);
        }
    }
}
