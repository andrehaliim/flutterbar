using System.Text;
using StatefulBar.Cli;
using StatefulBar.Core.Ipc;

Console.OutputEncoding = new UTF8Encoding(false);
return await CliApp.RunAsync(args, new IpcClient(PipeNames.ForCurrentUser()), Console.Out, Console.Error);
