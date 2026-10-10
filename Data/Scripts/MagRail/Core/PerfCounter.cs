using System;
using System.Diagnostics;

namespace MagRail
{
    // Time the mod spends in one place per tick or frame (chat /magrail): average and worst over the last
    // Window calls, so the numbers are steady enough to read.
    public class PerfCounter
    {
        const int Window = 600;

        readonly Stopwatch watch = new Stopwatch();
        double sum, worst;
        int count;

        public double Average { get; private set; }     // ms
        public double Worst { get; private set; }       // ms

        public void Start()
        {
            watch.Restart();
        }

        public void Stop()
        {
            double ms = watch.Elapsed.TotalMilliseconds;
            sum += ms;
            worst = Math.Max(worst, ms);
            if (++count < Window)
                return;
            Average = sum / count;
            Worst = worst;
            sum = worst = 0;
            count = 0;
        }
    }
}
