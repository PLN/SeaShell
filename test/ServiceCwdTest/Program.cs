using SeaShell.ServiceHost;

// Minimal ServiceHost binary that runs a script via RunScript().
// When started by SCM/systemd, CWD is system32 or / — the script
// must still be found relative to the exe's directory.

return await new ServiceHostBuilder()
	.ServiceName("seashell-cwd-test")
	.DisplayName("SeaShell CWD Test")
	.Description("Integration test: script resolution when CWD != exe dir")
	.RunScript("cwd-test-script.cs")
	.RunAsync(args);
