namespace DeskLink;

static class Program
{
    internal static System.Diagnostics.ProcessStartInfo StartSelf(string argument)
    {
        var info = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(Program).Assembly.Location);
        info.ArgumentList.Add(argument);
        return info;
    }
    [STAThread]
    static int Main(string[] args)
    {
        if (args is ["--restore-guard"]) { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); return RecoveryGuard.Run(); }
        if (args.Length > 0 && args[0] is "--pointer-check" or "--pointer-listen" or "--pointer-join")
            return PointerProbe.Run(args).GetAwaiter().GetResult();
        ApplicationConfiguration.Initialize();
        if (args is ["--projection-check"]) return ProjectionChecks.Run(capture: true);
        if (args is ["--projection-loopback-check"]) return ProjectionChecks.Run(capture: true, recordInput: true);
        if (args is ["--caption-input-check"]) return WindowChecks.CheckCaptionInput();
        if (args is ["--wheel-input-check"]) return WindowChecks.CheckCaptionInput(wheel: true);
        if (args is [var drag, var host, var dragPort, ..] && drag is "--remote-drag-listen" or "--brave-drag-listen" or "--remote-drag-join" &&
            int.TryParse(dragPort, out int portNumber) && (args.Length == 3 || args is [_, _, _, "--flick" or "--wide-flick" or "--approach" or "--latency-flick" or "--large-latency-flick"]))
        {
            StallLog.Timing = args.Last() is "--latency-flick" or "--large-latency-flick";
            return WindowChecks.CheckRemoteDrag(drag != "--remote-drag-join", host, portNumber, drag == "--brave-drag-listen",
                flick: args.Length == 4, wide: args.Last() == "--wide-flick", approach: args.Last() == "--approach", large: args.Last() == "--large-latency-flick");
        }
        if (args is [var test, var ip, var testPort, ..] && test is "--4k-listen" or "--4k-join" && int.TryParse(testPort, out int p))
        {
            var codec = FrameCodec.Av1;
            if (args.Length > 4 || (args.Length == 4 && (!Enum.TryParse(args[3], true, out codec) ||
                codec is not (FrameCodec.Av1 or FrameCodec.Hevc or FrameCodec.H264 or FrameCodec.Auto))))
            {
                Console.Error.WriteLine("Choose AV1, HEVC, H264 or Auto for the 4K check.");
                return 1;
            }
            return WindowChecks.Run4K(test == "--4k-listen", ip, p, codec);
        }
        if (args is [var mode, var address, var port] && mode is "--window-listen" or "--window-join" or "--settings-listen" or "--settings-join" or "--explorer-listen" or "--explorer-join" && int.TryParse(port, out var number))
            return WindowChecks.RunLan(mode.EndsWith("-listen"), address, number, mode.StartsWith("--settings-"), mode.StartsWith("--explorer-"));
        if (args.Contains("--self-test")) return Checks.Run();
        if (args is ["--codec-check"]) return VideoChecks.Run();
        if (args is ["--codec-benchmark"]) return Task.Run(VideoChecks.Benchmark).GetAwaiter().GetResult();
        if (args.Contains("--capture-test")) return WindowChecks.Run(args.Contains("--video"));
        if (args is ["--capture-fixture"]) return Checks.Fixture();
        Application.Run(new DeskForm());
        return 0;
    }
}
