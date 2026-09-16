using System.Globalization;
using System.IO;
using Peak.Cadder.Appearance;
using SwPart21 = Peak.Cadder.Sw.Part21;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// A STEP placement is in the unit of its representation context, and
    /// SolidWorks writes one context per part in that part's own units.
    /// Live 2026-09-14: the 2022 sample landing_gear.sldasm, inch parts under
    /// a metre assembly, matched 0 of 6 occurrences because both readers
    /// took every placement as millimetres.
    /// </summary>
    public class StepUnitsTests
    {
        private const string Header =
            "ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\n";

        private static Part21 Load(string data)
        {
            string path = Path.GetTempFileName();
            File.WriteAllText(path, Header + data + "ENDSEC;\nEND-ISO-10303-21;\n");
            return new Part21(path);
        }

        [Theory]
        [InlineData("( LENGTH_UNIT ( ) NAMED_UNIT ( * ) SI_UNIT ( .MILLI., .METRE. ) )", 1.0)]
        [InlineData("( LENGTH_UNIT ( ) NAMED_UNIT ( * ) SI_UNIT ( $, .METRE. ) )", 1000.0)]
        [InlineData("( LENGTH_UNIT ( ) NAMED_UNIT ( * ) SI_UNIT ( .CENTI., .METRE. ) )", 10.0)]
        public void SiLengthUnitsResolveToMillimetres(string unit, double mm)
        {
            var step = Load(
                "#1 =" + unit + ";\n"
                + "#2 = ( GEOMETRIC_REPRESENTATION_CONTEXT ( 3 ) "
                + "GLOBAL_UNIT_ASSIGNED_CONTEXT ( ( #1, #7, #8 ) ) "
                + "REPRESENTATION_CONTEXT ( 'NONE', 'WORKASPACE' ) );\n"
                + "#7 = ( NAMED_UNIT ( * ) PLANE_ANGLE_UNIT ( ) SI_UNIT ( $, .RADIAN. ) );\n"
                + "#8 = ( NAMED_UNIT ( * ) SI_UNIT ( $, .STERADIAN. ) SOLID_ANGLE_UNIT ( ) );\n");
            Assert.Equal(mm, step.LengthUnitMm(2), 9);
        }

        [Fact]
        public void AnInchContextIsTwentyFivePointFourMillimetres()
        {
            var step = Load(
                "#1 = ( CONVERSION_BASED_UNIT ( 'INCH', #3 ) LENGTH_UNIT ( ) NAMED_UNIT ( #4 ) );\n"
                + "#3 = LENGTH_MEASURE_WITH_UNIT ( LENGTH_MEASURE ( 25.4 ), #5 );\n"
                + "#4 = DIMENSIONAL_EXPONENTS ( 1.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 );\n"
                + "#5 = ( LENGTH_UNIT ( ) NAMED_UNIT ( * ) SI_UNIT ( .MILLI., .METRE. ) );\n"
                + "#2 = ( GEOMETRIC_REPRESENTATION_CONTEXT ( 3 ) "
                + "GLOBAL_UNIT_ASSIGNED_CONTEXT ( ( #1 ) ) "
                + "REPRESENTATION_CONTEXT ( 'NONE', 'WORKASPACE' ) );\n");
            Assert.Equal(25.4, step.LengthUnitMm(2), 9);
        }

        /// <summary>The placement's unit comes from the representation that
        /// lists it, found through the relationship: the parent's, not the
        /// child's. Here the parent is in inches and the child in
        /// millimetres.</summary>
        [Fact]
        public void APlacementTakesTheUnitOfTheRepresentationThatListsIt()
        {
            var step = Load(
                "#1 = ( CONVERSION_BASED_UNIT ( 'INCH', #3 ) LENGTH_UNIT ( ) NAMED_UNIT ( #4 ) );\n"
                + "#3 = LENGTH_MEASURE_WITH_UNIT ( LENGTH_MEASURE ( 25.4 ), #5 );\n"
                + "#4 = DIMENSIONAL_EXPONENTS ( 1.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 );\n"
                + "#5 = ( LENGTH_UNIT ( ) NAMED_UNIT ( * ) SI_UNIT ( .MILLI., .METRE. ) );\n"
                + "#10 = ( GEOMETRIC_REPRESENTATION_CONTEXT ( 3 ) GLOBAL_UNIT_ASSIGNED_CONTEXT ( ( #1 ) ) REPRESENTATION_CONTEXT ( 'P', 'W' ) );\n"
                + "#11 = ( GEOMETRIC_REPRESENTATION_CONTEXT ( 3 ) GLOBAL_UNIT_ASSIGNED_CONTEXT ( ( #5 ) ) REPRESENTATION_CONTEXT ( 'C', 'W' ) );\n"
                + "#20 = CARTESIAN_POINT ( 'NONE', ( 1.0, 2.0, 3.0 ) );\n"
                + "#21 = AXIS2_PLACEMENT_3D ( 'NONE', #20, $, $ );\n"
                + "#22 = CARTESIAN_POINT ( 'NONE', ( 0.0, 0.0, 0.0 ) );\n"
                + "#23 = AXIS2_PLACEMENT_3D ( 'NONE', #22, $, $ );\n"
                + "#30 = SHAPE_REPRESENTATION ( 'parent', ( #21 ), #10 );\n"
                + "#31 = SHAPE_REPRESENTATION ( 'child', ( #23 ), #11 );\n"
                + "#40 = ITEM_DEFINED_TRANSFORMATION ( 'NONE', 'NONE', #21, #23 );\n"
                + "#41 = ( REPRESENTATION_RELATIONSHIP ( 'NONE', 'NONE', #31, #30 ) "
                + "REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION ( #40 ) "
                + "SHAPE_REPRESENTATION_RELATIONSHIP ( ) );\n");
            Assert.Equal(25.4, step.PlacementUnitMm(41, 21), 9);
            Assert.Equal(1.0, step.PlacementUnitMm(41, 23), 9);

            // The exporter's own copy of the parser reads the same file the
            // same way.
            var sw = new SwPart21(step.Path);
            Assert.Equal(25.4, sw.PlacementUnitMm(41, 21), 9);
            Assert.Equal(1.0, sw.PlacementUnitMm(41, 23), 9);
        }
    }
}
