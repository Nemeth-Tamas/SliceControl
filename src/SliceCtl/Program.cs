using SliceControl;

try
{
    if (args.Length == 0)
    {
        PrintHelp();
        return 0;
    }

    string command =
        args[0].ToLowerInvariant();

    if (command == "service")
    {
        return HandleService(args);
    }

    if (command == "devices")
    {
        Console.WriteLine(
            SliceDevice.Discover());

        return 0;
    }

    SliceDevice slice =
        SliceDevice.Open();

    switch (command)
    {
        case "off":
        case "reset":
            slice.Lights.Reset();
            break;

        case "ring":
            slice.Lights.Ring();
            break;

        case "hello":
            slice.Lights.Hello();
            break;

        case "goodbye":
            slice.Lights.Goodbye();
            break;

        case "exit":
            slice.Lights.ExitAnimation();
            break;

        case "bar":
            slice.Lights.SetBar(
                ReadInt(args, 1, "value"));

            break;

        case "call":
            slice.Lights.SetCall(
                args.Length >= 2
                    ? ReadInt(args, 1, "value")
                    : 50);

            break;

        case "muted":
            slice.Lights.SetMutedCall(
                args.Length >= 2
                    ? ReadInt(args, 1, "value")
                    : 50);

            break;

        case "sweep":
            RunSweep(slice);
            break;

        case "watch":
            await WatchAsync(slice);
            break;

        case "watchraw":
            await WatchRawAsync(slice);
            break;

        case "raw":
            RunRaw(slice, args);
            break;

        case "rawff":
            RunRawFf(slice, args);
            break;

        default:
            Console.Error.WriteLine(
                $"Unknown command: {command}");

            PrintHelp();

            return 2;
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(
        $"{ex.GetType().Name}: {ex.Message}");

    return 1;
}

static int HandleService(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine(
            "Usage: slicectl service stop|start|query");

        return 2;
    }

    string action =
        args[1].ToLowerInvariant();

    string result =
        action switch
        {
            "stop" => HpTelephonyService.Stop(),
            "start" => HpTelephonyService.Start(),
            "query" => HpTelephonyService.Query(),

            _ => throw new ArgumentException(
                "Service action must be stop, start, or query.")
        };

    Console.WriteLine(result);

    return 0;
}

static async Task WatchAsync(
    SliceDevice slice)
{
    using var cts =
        new CancellationTokenSource();

    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cts.Cancel();
    };

    Console.WriteLine(
        "Watching Slice buttons. Press Ctrl+C to stop.");

    await slice.WatchButtonsAsync(
        ev =>
        {
            Console.WriteLine(
                $"{DateTime.Now:HH:mm:ss.fff}  {ev}");
        },
        cts.Token);
}

static async Task WatchRawAsync(
    SliceDevice slice)
{
    using var cts =
        new CancellationTokenSource();

    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cts.Cancel();
    };

    Console.WriteLine(
        "Watching raw Slice HID reports. Press Ctrl+C to stop.");

    await slice.WatchRawAsync(
        report =>
        {
            Console.WriteLine(
                $"{DateTime.Now:HH:mm:ss.fff}  {report}");
        },
        cts.Token);
}

static void RunSweep(
    SliceDevice slice)
{
    for (int i = 0; i <= 100; i++)
    {
        slice.Lights.SetBar(i);
        Thread.Sleep(15);
    }

    for (int i = 100; i >= 0; i--)
    {
        slice.Lights.SetBar(i);
        Thread.Sleep(15);
    }
}

static void RunRaw(
    SliceDevice slice,
    string[] args)
{
    if (args.Length != 9)
    {
        throw new ArgumentException(
            "Usage: slicectl raw FE 00 07 00 32 00 00 00");
    }

    byte[] report =
        args
            .Skip(1)
            .Select(ParseHexByte)
            .ToArray();

    slice.Raw.SendCollection03(report);

    Console.WriteLine(
        $"COL03 OUT: {BitConverter.ToString(report).Replace("-", " ")}");
}

static void RunRawFf(
    SliceDevice slice,
    string[] args)
{
    if (args.Length != 2)
    {
        throw new ArgumentException(
            "Usage: slicectl rawff 00");
    }

    byte value =
        ParseHexByte(args[1]);

    slice.Raw.SendCollection04(value);

    Console.WriteLine(
        $"COL04 OUT: FF {value:X2}");
}

static byte ParseHexByte(string text)
{
    text =
        text
            .Trim()
            .Replace("0x", "", StringComparison.OrdinalIgnoreCase);

    return Convert.ToByte(
        text,
        16);
}

static int ReadInt(
    string[] args,
    int index,
    string name)
{
    if (args.Length <= index ||
        !int.TryParse(args[index], out int value))
    {
        throw new ArgumentException(
            $"Missing or invalid {name}.");
    }

    return value;
}

static void PrintHelp()
{
    Console.WriteLine(
"""
SliceControl CLI

Usage:

  slicectl devices

  slicectl service stop
  slicectl service start
  slicectl service query

  slicectl off
  slicectl ring
  slicectl hello
  slicectl goodbye
  slicectl exit

  slicectl bar <0-100>
  slicectl call [0-100]
  slicectl muted [0-100]

  slicectl sweep
  slicectl watch
  slicectl watchraw

  slicectl raw FE 00 07 00 32 00 00 00
  slicectl rawff 00
""");
}