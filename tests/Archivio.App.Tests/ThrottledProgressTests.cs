using Archivio.App;
using Archivio.Application.Abstractions;

namespace Archivio.App.Tests;

public sealed class ThrottledProgressTests
{
    [Fact]
    public void Report_ForwardsFirstAndFinalValuesWhileDroppingABurst()
    {
        var received = new List<AudiobookExecutionProgress>();
        var target = new InlineProgress<AudiobookExecutionProgress>(received.Add);
        var progress = new ThrottledProgress<AudiobookExecutionProgress>(
            target,
            TimeSpan.FromHours(1),
            value => value.ProcessedCount >= value.TotalCount);

        progress.Report(new AudiobookExecutionProgress(0, 10, "first.mp3", "Starting"));
        progress.Report(new AudiobookExecutionProgress(1, 10, "second.mp3", "Moving"));
        progress.Report(new AudiobookExecutionProgress(10, 10, "last.mp3", "Complete"));

        Assert.Collection(
            received,
            value => Assert.Equal(0, value.ProcessedCount),
            value => Assert.Equal(10, value.ProcessedCount));
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
