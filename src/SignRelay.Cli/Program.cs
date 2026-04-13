using System.CommandLine;
using SignRelay.Cli.Commands;

return await SubmitCommand.Build().InvokeAsync(args).ConfigureAwait(false);
