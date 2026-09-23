using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Peak.Cadder.Bridge
{
    /// <summary>
    /// Runs the listener's jobs on the SolidWorks thread, one at a time,
    /// and waits for each one no longer than its caller will.
    ///
    /// The listener answers every request on a thread of its own, so a
    /// ping never waits behind a job. The jobs still take turns. A job
    /// holds the turn until it has finished on the SolidWorks thread, also
    /// after its caller got a busy answer: a job queued behind one that is
    /// held up by a dialog box would otherwise run inside that dialog's
    /// message loop, in the middle of the first job.
    /// </summary>
    internal sealed class JobGate
    {
        private readonly Action<Action> _post;
        private readonly SemaphoreSlim _turn = new SemaphoreSlim(1, 1);

        /// <param name="post">Queues an action on the SolidWorks thread and
        /// returns at once.</param>
        public JobGate(Action<Action> post)
        {
            _post = post;
        }

        /// <summary>
        /// Runs <paramref name="job"/> on the SolidWorks thread and returns
        /// its reply, or a busy reply when the wait for its turn and the
        /// job together take longer than <paramref name="timeout"/>. A
        /// failing job throws here, the same as it would inline.
        /// </summary>
        public Dictionary<string, object> Run(Func<Dictionary<string, object>> job, TimeSpan timeout)
        {
            var clock = Stopwatch.StartNew();
            if (!_turn.Wait(timeout))
                return Busy("SolidWorks is busy with a different request and did not start "
                    + "this one in " + Seconds(timeout) + " s. Try again later.");

            Dictionary<string, object> result = null;
            Exception error = null;
            var done = new ManualResetEventSlim(false);
            try
            {
                _post(() =>
                {
                    try { result = job(); }
                    catch (Exception ex) { error = ex; }
                    finally
                    {
                        done.Set();
                        _turn.Release();
                    }
                });
            }
            catch
            {
                _turn.Release();
                throw;
            }

            var left = timeout - clock.Elapsed;
            if (!done.Wait(left > TimeSpan.Zero ? left : TimeSpan.Zero))
                return Busy("The request did not finish in " + Seconds(timeout) + " s. A dialog "
                    + "box in SolidWorks can stop a request. Close the dialog box, and the "
                    + "request continues.");
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
            return result;
        }

        private static Dictionary<string, object> Busy(string why)
        {
            return new Dictionary<string, object>
            {
                { "ok", false }, { "busy", true }, { "error", why },
            };
        }

        private static string Seconds(TimeSpan span)
        {
            return span.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }
}
