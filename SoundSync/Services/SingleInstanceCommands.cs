using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace SoundSync.Services
{
    /// <summary>
    /// Lets a second launch hand a command to the copy already running.
    ///
    /// The taskbar jump list - the menu you get when you right-click the taskbar button -
    /// can only start a program with arguments; it cannot talk to a live window. So the
    /// jump list starts SoundSync with a command, that second process passes it down this
    /// pipe and exits, and the running window acts on it.
    /// </summary>
    public static class SingleInstanceCommands
    {
        /// <summary>Argument the jump list uses to bring the window back to a sane size.</summary>
        public const string ResetWindowArgument = "--reset-window";

        private const string PipeName = "SoundSync.Commands";

        private static CancellationTokenSource? listener;

        /// <summary>Starts listening. <paramref name="onCommand"/> runs for each one received.</summary>
        public static void StartListening(Action<string> onCommand)
        {
            listener = new CancellationTokenSource();
            var token = listener.Token;

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var server = new NamedPipeServerStream(
                            PipeName, PipeDirection.In, 1,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                        await server.WaitForConnectionAsync(token);

                        using var reader = new StreamReader(server);
                        string? command = await reader.ReadLineAsync();
                        if (!string.IsNullOrWhiteSpace(command)) onCommand(command.Trim());
                    }
                    catch (OperationCanceledException) { return; }
                    catch
                    {
                        // A malformed or abandoned connection must not kill the listener.
                        await Task.Delay(200, CancellationToken.None);
                    }
                }
            }, token);
        }

        public static void StopListening()
        {
            try { listener?.Cancel(); } catch { }
            listener = null;
        }

        /// <summary>
        /// Hands a command to the running copy. Returns false when nothing is listening,
        /// which means this process is the first one and should carry on starting up.
        /// </summary>
        public static bool SendToRunningInstance(string command)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(600);

                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(command);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
