using Archivio.Application.Abstractions;
using Archivio.Application.Services;
using Archivio.Domain;

namespace Archivio.UnitTests;

public sealed class AudiobookBatchPlanningServiceTests
{
    [Fact]
    public async Task PrepareBatch_CreatesAReadOnlyFileLevelOperation()
    {
        var fixture = CreateCandidate("plan-1", "Author", "Book", ready: true);
        var store = new StubDecisionStore();
        var service = new AudiobookBatchPlanningService(store);

        var result = await service.PrepareBatchAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate],
            [fixture.Item]);

        var plan = Assert.IsType<AudiobookBatchPlan>(Assert.Single(result).BatchPlan);
        var operation = Assert.Single(plan.Operations);
        Assert.Equal(AudiobookBatchValidationStatus.Ready, plan.ValidationStatus);
        Assert.Equal(AudiobookBatchDecision.Pending, plan.Decision);
        Assert.Equal(fixture.Item.RelativePath, operation.SourceRelativePath);
        Assert.Equal(
            Path.Combine("Author", "Book", "Author - Book.mp3"),
            operation.DestinationRelativePath);
        Assert.Equal(AudiobookFileOperationKind.MoveAndRename, operation.Kind);
        Assert.Equal(fixture.Item.FullPath, fixture.Candidate.Parts[0].MediaItem.FullPath);
    }

    [Fact]
    public async Task PrepareBatch_BlocksPlansThatShareADestination()
    {
        var first = CreateCandidate("plan-1", "Author", "Book", ready: true, sourceFile: "First.mp3");
        var second = CreateCandidate(
            "plan-2",
            "Author",
            "Book",
            ready: true,
            sourceFile: "Second.mp3",
            sourceId: first.SourceId,
            root: first.Root);
        var service = new AudiobookBatchPlanningService(new StubDecisionStore());

        var result = await service.PrepareBatchAsync(
            first.SourceId,
            first.Root,
            [first.Candidate, second.Candidate],
            [first.Item, second.Item]);

        Assert.All(result, candidate =>
        {
            Assert.Equal(AudiobookBatchValidationStatus.Conflict, candidate.BatchPlan?.ValidationStatus);
            Assert.Contains(
                candidate.BatchPlan!.Warnings,
                warning => warning.Contains("same destination", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task ApprovedDecision_RoundTripsWhenThePreviewIsUnchanged()
    {
        var fixture = CreateCandidate("plan-1", "Author", "Book", ready: true);
        var store = new StubDecisionStore();
        var service = new AudiobookBatchPlanningService(store);
        var prepared = await service.PrepareBatchAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate],
            [fixture.Item]);

        var approved = await service.SetDecisionAsync(
            fixture.SourceId,
            prepared,
            ["plan-1"],
            AudiobookBatchDecision.Approved);
        var restored = await service.PrepareBatchAsync(
            fixture.SourceId,
            fixture.Root,
            approved,
            [fixture.Item]);

        Assert.Equal(AudiobookBatchDecision.Approved, Assert.Single(restored).BatchPlan?.Decision);
    }

    [Fact]
    public async Task ReviewRequiredPlan_CannotBeApproved()
    {
        var fixture = CreateCandidate("plan-1", "Author", "Book", ready: false);
        var service = new AudiobookBatchPlanningService(new StubDecisionStore());
        var prepared = await service.PrepareBatchAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate],
            [fixture.Item]);

        var result = await service.SetDecisionAsync(
            fixture.SourceId,
            prepared,
            ["plan-1"],
            AudiobookBatchDecision.Approved);

        var plan = Assert.IsType<AudiobookBatchPlan>(Assert.Single(result).BatchPlan);
        Assert.Equal(AudiobookBatchValidationStatus.ReviewRequired, plan.ValidationStatus);
        Assert.Equal(AudiobookBatchDecision.Pending, plan.Decision);
    }

    [Fact]
    public async Task PrepareBatch_BlocksAnOccupiedIndexedDestination()
    {
        var fixture = CreateCandidate("plan-1", "Author", "Book", ready: true);
        var now = DateTime.UtcNow;
        var targetRelativePath = Path.Combine("Author", "Book", "Author - Book.mp3");
        var occupant = new MediaItem(
            fixture.SourceId,
            Path.Combine(fixture.Root, targetRelativePath),
            targetRelativePath,
            200,
            now,
            now,
            now);
        var service = new AudiobookBatchPlanningService(new StubDecisionStore());

        var result = await service.PrepareBatchAsync(
            fixture.SourceId,
            fixture.Root,
            [fixture.Candidate],
            [fixture.Item, occupant]);

        var plan = Assert.IsType<AudiobookBatchPlan>(Assert.Single(result).BatchPlan);
        Assert.Equal(AudiobookBatchValidationStatus.Conflict, plan.ValidationStatus);
        Assert.Contains(
            plan.Warnings,
            warning => warning.Contains("already occupied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareBatch_AssignsStableSequenceNamesToGroupedFiles()
    {
        var first = CreateCandidate("plan-1", "Author", "Book", ready: true, sourceFile: "Part 01.mp3");
        var second = CreateCandidate(
            "plan-1",
            "Author",
            "Book",
            ready: true,
            sourceFile: "Part 02.mp3",
            sourceId: first.SourceId,
            root: first.Root);
        var primaryProposal = first.Candidate.OrganisationProposal! with
        {
            RelatedCandidateCount = 2,
            SourceFileCount = 2,
            SuggestedFileNamePattern = "001 - Book{original extension}"
        };
        var candidates = new[]
        {
            first.Candidate with { OrganisationProposal = primaryProposal },
            second.Candidate with
            {
                OrganisationProposal = primaryProposal with { IsPrimaryCandidate = false }
            }
        };
        var service = new AudiobookBatchPlanningService(new StubDecisionStore());

        var result = await service.PrepareBatchAsync(
            first.SourceId,
            first.Root,
            candidates,
            [first.Item, second.Item]);

        var plan = Assert.IsType<AudiobookBatchPlan>(result[0].BatchPlan);
        Assert.Collection(
            plan.Operations,
            operation => Assert.EndsWith(
                Path.Combine("Author", "Book", "001 - Book.mp3"),
                operation.DestinationRelativePath,
                StringComparison.Ordinal),
            operation => Assert.EndsWith(
                Path.Combine("Author", "Book", "002 - Book.mp3"),
                operation.DestinationRelativePath,
                StringComparison.Ordinal));
    }

    private static CandidateFixture CreateCandidate(
        string planKey,
        string author,
        string title,
        bool ready,
        string sourceFile = "Original.mp3",
        Guid? sourceId = null,
        string? root = null)
    {
        var librarySourceId = sourceId ?? Guid.NewGuid();
        var libraryRoot = root ?? Path.Combine(
            Path.GetTempPath(),
            "Archivio.Tests",
            Guid.NewGuid().ToString("N"));
        var now = DateTime.UtcNow;
        var relativePath = Path.Combine("Incoming", sourceFile);
        var item = new MediaItem(
            librarySourceId,
            Path.Combine(libraryRoot, relativePath),
            relativePath,
            100,
            now,
            now,
            now);
        var metadata = new LocalMediaMetadata(
            item.FullPath,
            new MetadataValue(title, MetadataValueSource.EmbeddedTag),
            new MetadataValue(author, MetadataValueSource.EmbeddedTag),
            new MetadataValue(null, MetadataValueSource.None),
            new MetadataValue(null, MetadataValueSource.None),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            [],
            []);
        var proposal = new AudiobookOrganisationProposal(
            planKey,
            author,
            title,
            null,
            "Uncategorised",
            Path.Combine(author, title),
            $"{author} - {title}.mp3",
            AudiobookOrganisationAction.MoveAndRename,
            1,
            1,
            true,
            false,
            ready ? 1m : 0.60m,
            ready,
            false,
            [],
            ready ? [] : ["Review required."],
            now);
        var candidate = new AudiobookCandidateGroup(
            $"{author} - {title}",
            author,
            title,
            MetadataValueSource.EmbeddedTag,
            MetadataValueSource.EmbeddedTag,
            true,
            [new AudiobookCandidatePart(item, 1, true, metadata)],
            ready ? 1m : 0.60m,
            ready ? [] : ["Review required."])
        {
            OrganisationProposal = proposal
        };
        return new CandidateFixture(librarySourceId, libraryRoot, item, candidate);
    }

    private sealed record CandidateFixture(
        Guid SourceId,
        string Root,
        MediaItem Item,
        AudiobookCandidateGroup Candidate);

    private sealed class StubDecisionStore : IAudiobookBatchDecisionStore
    {
        private readonly Dictionary<string, AudiobookBatchDecisionEntry> _entries =
            new(StringComparer.Ordinal);

        public Task<IReadOnlyDictionary<string, AudiobookBatchDecisionEntry>> LoadAsync(
            Guid librarySourceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, AudiobookBatchDecisionEntry>>(_entries);

        public Task SaveAsync(
            Guid librarySourceId,
            IReadOnlyCollection<AudiobookBatchDecisionEntry> entries,
            CancellationToken cancellationToken = default)
        {
            foreach (var entry in entries)
            {
                _entries[entry.PlanKey] = entry;
            }

            return Task.CompletedTask;
        }
    }
}
