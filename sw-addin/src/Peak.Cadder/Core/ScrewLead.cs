using System;

namespace Peak.Cadder.Core
{
    /// <summary>
    /// The lead of a screw mate: metres of travel for one turn, signed.
    ///
    /// SolidWorks states a screw mate one of two ways, and they do not use
    /// the same unit.
    ///
    /// * Distance per revolution is in METRES, like every other length the
    ///   API hands over. Pinned on corpus 09 (2026-08-22): the mate was
    ///   built as 2 mm per revolution and the value arrived as 0.002.
    /// * Revolutions per unit length counts turns per ONE OF THE DOCUMENT'S
    ///   OWN LINEAR UNITS, which is what the API help says and what the
    ///   live wrench sample proves. Read as turns per metre, a wrench whose
    ///   screw advances one unit per turn came out at one METRE per turn:
    ///   Oscar, 2026-09-16, "the created screw joint doesn't rotate
    ///   anywhere near as much as it does in SW, it only barely turns".
    ///   The rig needed a thousandth of a turn to drive the jaw through its
    ///   whole travel.
    ///
    /// No SolidWorks types here: the caller reads the document's unit and
    /// hands over its size in metres, and the rule is then arithmetic the
    /// tests can run.
    /// </summary>
    public static class ScrewLead
    {
        /// <summary>
        /// The signed lead, or null when the mate states nothing usable.
        ///
        /// The sign is chirality, so no axis sense belongs in it. Pinned on
        /// corpus 09 (2026-08-22): a default screw mate behaved as a
        /// right-hand thread in SolidWorks while Reverse read true, so
        /// Reverse means advance along the turn's right-hand direction.
        /// </summary>
        public static double? Metres(
            double value, bool distancePerRevolution, bool reverse, double unitMetres)
        {
            double lead;
            if (distancePerRevolution)
            {
                lead = value;
            }
            else
            {
                if (Math.Abs(value) < 1e-12) return null;
                if (!(unitMetres > 0)) unitMetres = 1.0;
                lead = unitMetres / value;
            }
            if (Math.Abs(lead) < 1e-12) return null;
            return reverse ? lead : -lead;
        }
    }
}
