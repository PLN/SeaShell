using SeaShell.Installer;
using Xunit;

namespace EngineTest;

public class InstallRunSummaryTests
{
	private static ScopeResolution Recording(ScopeParts parts) => new()
	{
		Parts = parts,
		Unresolved = ScopeParts.None,
		Source = ScopeSource.Recorded,
		Record = true,
		LeftBehind = ScopeParts.None,
	};

	[Fact]
	public void Record_WhatWasActuallyInstalled()
	{
		var summary = new InstallRunSummary();
		summary.Add(ScopeParts.User | ScopeParts.Elevator, PassResult.Installed);
		summary.Add(ScopeParts.System, PassResult.Installed);

		var parts = ScopeRecording.PartsToRecord(Recording(ScopeParts.User), summary, knownBefore: ScopeParts.User);

		Assert.Equal(ScopeParts.User | ScopeParts.Elevator | ScopeParts.System, parts);
	}

	[Fact]
	public void AlternateAccount_ElevatorSkipped_IsNotRecorded()
	{
		// Helper (other account) installed SYSTEM; the invoker then did the user pass.
		var summary = new InstallRunSummary();
		summary.Add(ScopeParts.System, PassResult.Installed);
		summary.Add(ScopeParts.User, PassResult.Installed);
		var all = ScopeParts.User | ScopeParts.Elevator | ScopeParts.System;

		var parts = ScopeRecording.PartsToRecord(Recording(all) with { Source = ScopeSource.All }, summary, knownBefore: ScopeParts.None);

		Assert.Equal(ScopeParts.User | ScopeParts.System, parts);
	}

	[Fact]
	public void FailedPart_KeptOnlyWhenItWasKnownBefore()
	{
		var summary = new InstallRunSummary();
		summary.Add(ScopeParts.User, PassResult.Installed);
		summary.Add(ScopeParts.System, PassResult.Failed);

		// Remembered SYSTEM part survives one failed update...
		Assert.Equal(ScopeParts.User | ScopeParts.System,
			ScopeRecording.PartsToRecord(Recording(ScopeParts.User | ScopeParts.System), summary, knownBefore: ScopeParts.User | ScopeParts.System));
		// ...but a part that never got installed is not recorded.
		Assert.Equal(ScopeParts.User,
			ScopeRecording.PartsToRecord(Recording(ScopeParts.User | ScopeParts.System), summary, knownBefore: ScopeParts.User));
		Assert.Equal(1, summary.ExitCode);
	}

	[Theory]
	[InlineData(PassResult.Failed)]
	[InlineData(PassResult.SkippedNewer)]   // a newer bootstrapper owns this install now
	public void UserInstallNotInPlace_LeavesTheFileAlone(PassResult userResult)
	{
		var summary = new InstallRunSummary();
		summary.Add(ScopeParts.User, userResult);

		Assert.Null(ScopeRecording.PartsToRecord(Recording(ScopeParts.User), summary, ScopeParts.User));
	}

	[Fact]
	public void UpToDate_OnlyWhenEveryRequestedPartWasSatisfied()
	{
		var all = new InstallRunSummary();
		all.Add(ScopeParts.User | ScopeParts.Elevator, PassResult.Satisfied);
		all.Add(ScopeParts.System, PassResult.Satisfied);
		Assert.True(all.AllSatisfied(ScopeParts.User | ScopeParts.Elevator | ScopeParts.System));

		// A missing part at the same version was installed: something was done.
		var partial = new InstallRunSummary();
		partial.Add(ScopeParts.User, PassResult.Satisfied);
		partial.Add(ScopeParts.System, PassResult.Installed);
		Assert.False(partial.AllSatisfied(ScopeParts.User | ScopeParts.System));
	}

	[Fact]
	public void ResultFile_RoundTripsTheHelpersReport()
	{
		var helper = new InstallRunSummary
		{
			UserSkipped = true,
			Detection = new ScopeDetection(PartPresence.Unknown, PartPresence.Present),
		};
		helper.Add(ScopeParts.System, PassResult.Satisfied);
		helper.Add(ScopeParts.Elevator, PassResult.Failed);

		var parsed = InstallRunSummary.Parse(helper.Serialize());

		Assert.Equal(ScopeParts.System, parsed.Completed);
		Assert.Equal(ScopeParts.System, parsed.Satisfied);
		Assert.Equal(ScopeParts.Elevator, parsed.Failed);
		Assert.True(parsed.UserSkipped);
		Assert.Equal(new ScopeDetection(PartPresence.Unknown, PartPresence.Present), parsed.Detection);
	}

	[Fact]
	public void PartSkippedForANewerVersion_StaysRecorded()
	{
		// Review finding F1: scope user,elevator,system; SYSTEM already on X+1; `update --version X`.
		var summary = new InstallRunSummary();
		summary.Add(ScopeParts.User | ScopeParts.Elevator, PassResult.Installed);
		summary.Add(ScopeParts.System, PassResult.SkippedNewer);
		var all = ScopeParts.User | ScopeParts.Elevator | ScopeParts.System;

		Assert.Equal(all, ScopeRecording.PartsToRecord(Recording(all), summary, knownBefore: all));
	}

	[Fact]
	public void UnchangedScope_IsNotRewritten()
	{
		// E2E scenario 2: a same-version update rewrote install.scope with the same content.
		var all = ScopeParts.User | ScopeParts.Elevator | ScopeParts.System;
		var summary = new InstallRunSummary();
		summary.Add(all, PassResult.Satisfied);

		Assert.Null(ScopeRecording.PartsToRecord(Recording(all), summary, all, new ScopeFileRead(ScopeFileState.Read, all)));
		// A file that says less than what is installed is still corrected.
		Assert.Equal(all, ScopeRecording.PartsToRecord(Recording(all), summary, all, new ScopeFileRead(ScopeFileState.Read, ScopeParts.User)));
	}
}
