using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.NET.Common;
using BepInExResoniteShim;
using Elements.Core;

namespace ALogSender;

[ResonitePlugin(PluginMetadata.GUID, PluginMetadata.NAME, PluginMetadata.VERSION, PluginMetadata.AUTHORS, PluginMetadata.REPOSITORY_URL)]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger;

    private static ConfigEntry<int> _port;
    private static ConfigEntry<bool> _logToConsole;

    private static UdpClient _udpClient;
    private static CancellationTokenSource _changeCts;
    private static ProcessStartInfo _startInfo;

    private static Process _aLogViewerProcess;

    public override void Load()
    {
        // Plugin startup logic
        Logger = base.Log;

        _port = Config.Bind("General", "Port", 9999, new ConfigDescription("Port to listen on", new AcceptableValueRange<int>(1024, 65535)));
        _logToConsole = Config.Bind("General", "LogToConsole", true, new ConfigDescription("Log to the BepInEx Console?"));
        _port.SettingChanged += (_, _) =>
        {
            _changeCts?.Cancel();
            _changeCts?.Dispose();
            _changeCts = new CancellationTokenSource();

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(3000);

                    StartALogViewer();
                    StartUdpClient(_port.Value);
                }
                catch (OperationCanceledException) { }
            }, _changeCts.Token);
        };

        StartALogViewer();
        StartUdpClient(_port.Value);

        BepInEx.Logging.Logger.Listeners.Add(new ALogListener());
        
        UniLog.OnLog += msg => UdpLog(msg, LogLevel.Message, _logToConsole.Value);
        UniLog.OnWarning += msg => UdpLog(msg, LogLevel.Warning, _logToConsole.Value);
        UniLog.OnError += msg => UdpLog(msg, LogLevel.Error, _logToConsole.Value);

        Logger.LogInfo($"Plugin {PluginMetadata.GUID} is loaded!");
    }

    private static void StartALogViewer()
    {
        if (_startInfo == null)
        {
            string mainDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (mainDir == null)
            {
                Logger.LogError("Could not get main directory");
                return;
            }

            string aLogViewerPath = Path.Combine(mainDir, "ALogViewer.exe");
            if (!File.Exists(aLogViewerPath))
            {
                Logger.LogError($"ALogViewer.exe not found - {aLogViewerPath}");
                return;
            }

            _startInfo = new ProcessStartInfo
            {
                FileName = aLogViewerPath,
                WorkingDirectory = mainDir,
                Arguments = _port.Value.ToString(),
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Normal,
                CreateNoWindow = false
            };
        }
        else
        {
            _startInfo.Arguments = _port.Value.ToString();
        }

        if (_startInfo != null)
        {
            _aLogViewerProcess?.Kill();
            _aLogViewerProcess?.Dispose();
            _aLogViewerProcess = Process.Start(_startInfo);
        }
    }

    private static void StartUdpClient(int port)
    {
        try
        {
            _udpClient?.Close();
            _udpClient?.Dispose();
            _udpClient = new UdpClient
            {
                EnableBroadcast = true,
            };
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            Logger.LogMessage("UDP Client started");
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error in UDP Client: {ex.Message}");
        }
    }

    private static void UdpLog(string msg, LogLevel level = LogLevel.Info, bool logToLocal = false)
    {
        UdpLogInternal(msg, level, logToLocal);
    }

    private static void UdpLogInternal(string message, LogLevel level, bool logToLocal)
    {
        try
        {
            if (logToLocal)
            {
                Logger.Log(level, message);
            }

            if (_udpClient == null) return;

            byte[] sendBytes = Encoding.UTF8.GetBytes($"{DateTime.Now.ToMillisecondTimeString()} {message}");
            _udpClient.Send(sendBytes, sendBytes.Length, "255.255.255.255", _port.Value);
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error sending message to UDP Server: {ex.Message}");
        }
    }

    private class ALogListener : ILogListener
    {
        public LogLevel LogLevelFilter => LogLevel.All;

        public void LogEvent(object sender, LogEventArgs args)
        {
            if (sender is not ManualLogSource logSource || logSource == Logger) return;
            
            Plugin.UdpLog($"[{args.Level.ToString().ToUpper()}][{logSource.SourceName}] {args.Data}");
        }

        public void Dispose() { }
    }
}