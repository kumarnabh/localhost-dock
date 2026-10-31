using LocalhostDock.Cli;

Console.OutputEncoding = System.Text.Encoding.UTF8;
return await CliRunner.RunAsync(args, Console.Out, Console.Error);
