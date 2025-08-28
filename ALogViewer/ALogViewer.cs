using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ALogViewer;

public static class ALogViewer
{
    private static bool _stopping;
    private static ConsoleColor _currentColor = ConsoleColor.Gray;
    private static UdpClient _udpClient;
    private static string _lastLogMessage = string.Empty;
    private const string Pattern = @"\d{1,2}:\d{1,2}:\d{1,2}(?:\s[APap][Mm])?(?:\.\d{1,3})?(?:\s+\(\s*-*\d+\s?FPS\s?\))?\s*";

    private static bool _run;
    // private const string Pattern = @"\d{1,2}:\d{1,2}:\d{1,2}(\s[APap][Mm])?\.\d{1,3}";

    private static void Main(string[] args)
    {
        try
        {
            int port = 9999;
            try
            {
                port = args.Length > 0 ? int.Parse(args[0]) : 9999;
            }
            catch (Exception e)
            {
                PrintMessage($"Error parsing Port: {e.Message}", ConsoleColor.Red);
                PrintMessage($"Using default Port: {port}", ConsoleColor.Red);
                port = 9999;
            }

            if (args.Length > 1)
            {
                _run = bool.Parse(args[1]);
            }

            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, port));

            Console.Title = "Resonite Console";
            Console.OutputEncoding = Encoding.UTF8;
            PrintMessage("LogViewer started. Press Enter to exit...", ConsoleColor.Green);

            Task.Run(ReceiveLogsAsync);
        }
        catch (Exception e)
        {
            PrintMessage($"An Error has occured durring Init: {e}", ConsoleColor.Red);
        }

        Task.Run(async () =>
        {
            int parentPid = GetParentProcessId(Environment.ProcessId);
            while (true)
            {
                try
                {
                    Process.GetProcessById(parentPid);
                }
                catch (ArgumentException)
                {
                    _stopping = true;
                }

                await Task.Delay(1000);
            }
        });

        while (!_stopping)
        {
            try
            {
                if (Console.KeyAvailable)
                {
                    ConsoleKeyInfo key = Console.ReadKey(true);

                    if (key.Key == ConsoleKey.Enter) break;

                    if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.L)
                    {
                        Console.Clear();
                        PrintMessage("Console cleared! Press Enter to break or Ctrl+L to clear again.", ConsoleColor.Green);
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // ignored
            }

            Thread.Sleep(50);
        }

        _stopping = true;
        _udpClient.Dispose();
        _udpClient.Close();
    }

    private static async Task ReceiveLogsAsync()
    {
        while (!_stopping)
        {
            try
            {
                UdpReceiveResult result = await _udpClient.ReceiveAsync();
                byte[] receiveBytes = result.Buffer;
                string receivedMessage = Encoding.UTF8.GetString(receiveBytes);

                if (string.IsNullOrWhiteSpace(receivedMessage) || !IsLocalIPv4(result.RemoteEndPoint.Address) && !_run) continue;
                OnLogMessage(receivedMessage);
                _lastLogMessage = receivedMessage;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                const string disconnectMessage = "Disconnected from server. Attempting to reconnect...";
                if (disconnectMessage != _lastLogMessage)
                {
                    PrintMessage(disconnectMessage, ConsoleColor.Red);
                    _lastLogMessage = disconnectMessage;
                }

                await Task.Delay(1000);
            }
            catch (ObjectDisposedException) when (_stopping)
            {
                break;
            }
            catch (Exception ex)
            {
                PrintMessage($"Error receiving log entry: {ex.Message}", ConsoleColor.Red);
            }
        }
    }

    private static bool IsLocalIPv4(IPAddress address)
    {
        foreach (IPAddress ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
        {
            if (ip.AddressFamily == AddressFamily.InterNetwork && ip.Equals(address))
            {
                return true;
            }
        }

        return false;
    }

    private static void OnLogMessage(string message)
    {
        string cleanedMessage = Regex.Replace(message, Pattern, "");
        bool hasTimestampAndFps = Regex.IsMatch(message, Pattern);

        if (hasTimestampAndFps)
        {
            if (IsValidLog(cleanedMessage) && MatchesLogPatterns(cleanedMessage))
            {
                FormatModLoaderLog(cleanedMessage);
            }
            else if (IsValidLog(cleanedMessage))
            {
                PrintMessage(cleanedMessage);
            }
        }
        else
        {
            // This is a continuation of the previous message
            PrintMessage(cleanedMessage, _currentColor);
        }
    }

    private static void FormatModLoaderLog(string message)
    {
        ConsoleColor consoleColor = ConsoleColor.Gray;

        foreach (KeyValuePair<Regex, ConsoleColor> pattern in LogPatterns)
        {
            if (pattern.Key.IsMatch(message))
            {
                consoleColor = pattern.Value;
                break;
            }
        }

        _currentColor = consoleColor;
        PrintMessage(message, consoleColor);
    }

    private static bool MatchesLogPatterns(string message)
    {
        foreach (Regex pattern in LogPatterns.Keys)
        {
            if (pattern.IsMatch(message)) return true;
        }

        return false;
    }

    private static bool IsValidLog(string message)
    {
        foreach (Regex pattern in InvalidPatterns)
        {
            if (pattern.IsMatch(message)) return false;
        }

        return true;
    }

    private static void PrintMessage(string message, ConsoleColor color = ConsoleColor.Gray)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    private static readonly Dictionary<Regex, ConsoleColor> LogPatterns = new Dictionary<Regex, ConsoleColor>
    {
        // Errors & fatal
        { new Regex(@"\[(?:error|fatal)\]|failed load: could not gather|exception(?: in runningcoroutine)?|<\w{32}>:0", RegexOptions.IgnoreCase | RegexOptions.Compiled), ConsoleColor.Red },
        { new Regex(@"restoring currently updating root", RegexOptions.IgnoreCase | RegexOptions.Compiled), ConsoleColor.DarkRed },

        // Informational
        { new Regex(@"\[info\]", RegexOptions.IgnoreCase | RegexOptions.Compiled), ConsoleColor.Green },
        { new Regex(@"\[message\]", RegexOptions.IgnoreCase | RegexOptions.Compiled), ConsoleColor.DarkGreen },

        // Debugging
        { new Regex(@"\[(?:debug|trace)\]|resonite \(unity\) game pack", RegexOptions.IgnoreCase | RegexOptions.Compiled), ConsoleColor.Blue },

        // Warnings
        { new Regex(@"\[(?:warn|warning)\]|updated:\s?https|lastmodifyinguser|broadcastkey|unresolved|can be modified only through the drive reference", RegexOptions.IgnoreCase | RegexOptions.Compiled), ConsoleColor.Yellow },
        { new Regex(@"user (?:join|joined|spawn|spawned)|spawning user|User\s+(\S+)\s+Role:", RegexOptions.IgnoreCase | RegexOptions.Compiled), ConsoleColor.DarkYellow },

        // Status updates
        { new Regex(@"signalr|clearing expired status|status (?:before|after) clearing|status initialized|updated:\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled), ConsoleColor.DarkMagenta },

        // Direct user messages
        { new Regex(@"sendstatustouser:", RegexOptions.IgnoreCase | RegexOptions.Compiled), ConsoleColor.Magenta },

        // Refresh actions
        { new Regex(@"running refresh on:", RegexOptions.IgnoreCase | RegexOptions.Compiled), ConsoleColor.Cyan },
        { new Regex(@"loading object from record|loading from uri|loading from record|source record", RegexOptions.IgnoreCase | RegexOptions.Compiled), ConsoleColor.DarkCyan },
    };

    private static readonly Regex[] InvalidPatterns =
    {
        new Regex(@"session updated, forcing status update", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"\[debug\]\[resonitemodloader\]\s+intercepting call to appdomain\.getassemblies\(\)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"rebuild:", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"featureflag", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    private static int GetParentProcessId(int pid)
    {
        Process process = Process.GetProcessById(pid);
        PROCESS_BASIC_INFORMATION pbi = new PROCESS_BASIC_INFORMATION();
        int status = NtQueryInformationProcess(process.Handle, 0, ref pbi, Marshal.SizeOf(pbi), out _);
        if (status != 0) throw new InvalidOperationException("Unable to get parent process information.");

        return pbi.InheritedFromUniqueProcessId.ToInt32();
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }
}