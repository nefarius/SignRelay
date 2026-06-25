using System.CommandLine;
using SignRelay.Cli.Commands;

return await SubmitCommand.Build().Parse(args).InvokeAsync().ConfigureAwait(false);
