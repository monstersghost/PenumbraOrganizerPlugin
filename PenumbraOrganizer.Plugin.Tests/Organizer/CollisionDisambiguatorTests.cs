using System.Collections;
using PenumbraOrganizer.Plugin.Organizer;

namespace PenumbraOrganizer.Plugin.Tests.Organizer;

public class CollisionDisambiguatorTests
{
    private static OrganizerModRow MakeRow(string identifier, string name, string proposedPath) => new()
    {
        Identifier = identifier,
        Name = name,
        Author = "SomeAuthor",
        CurrentPath = proposedPath,
        ProposedPath = proposedPath,
    };

    // Wraps a list and throws if GetEnumerator() is called more than once, proving
    // Disambiguate materializes its input instead of enumerating it repeatedly.
    private sealed class SingleEnumerationGuard(IReadOnlyList<OrganizerModRow> rows) : IEnumerable<OrganizerModRow>
    {
        private bool _enumerated;

        public IEnumerator<OrganizerModRow> GetEnumerator()
        {
            if (_enumerated)
                throw new InvalidOperationException("Enumerated more than once.");
            _enumerated = true;
            return rows.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static OrganizerModRow MakeMovedRow(string identifier, string name, string currentPath, string proposedPath) => new()
    {
        Identifier = identifier,
        Name = name,
        Author = "SomeAuthor",
        CurrentPath = currentPath,
        ProposedPath = proposedPath,
    };

    [Fact]
    public void Disambiguate_RowAlreadyInPlace_WinsCanonicalOverExactIdentifierMatch()
    {
        // "AAA Foo" sits at the collision path already; "Foo" (Identifier == Name, the old
        // canonical rule's winner) is being moved in. Minimal churn: the row that does not
        // need any SetModPath call keeps the bare path; the arriving row gets the suffix.
        var inPlace = MakeMovedRow("AAA Foo", "Foo", "Creator/Foo", "Creator/Foo");
        var arriving = MakeMovedRow("Foo", "Foo", "Old/Foo", "Creator/Foo");

        CollisionDisambiguator.Disambiguate([inPlace, arriving]);

        Assert.Equal("Creator/Foo", inPlace.ProposedPath);
        Assert.Equal("Creator/Foo (2)", arriving.ProposedPath);
    }

    [Fact]
    public void Disambiguate_TwoWayCollisionWithExactIdentifierMatch_CanonicalStaysBareOtherGetsSuffix()
    {
        var canonical = MakeRow("Foo", "Foo", "Creator/Foo");
        var duplicate = MakeRow("Foo_2", "Foo", "Creator/Foo");

        CollisionDisambiguator.Disambiguate([canonical, duplicate]);

        Assert.Equal("Creator/Foo", canonical.ProposedPath);
        Assert.Equal("Creator/Foo (2)", duplicate.ProposedPath);
    }

    [Fact]
    public void Disambiguate_ThreeWayCollisionOneCanonical_OthersNumberedSequentially()
    {
        var canonical = MakeRow("Foo", "Foo", "Creator/Foo");
        var dupA = MakeRow("Foo_2", "Foo", "Creator/Foo");
        var dupB = MakeRow("Foo_3", "Foo", "Creator/Foo");

        CollisionDisambiguator.Disambiguate([canonical, dupA, dupB]);

        Assert.Equal("Creator/Foo", canonical.ProposedPath);
        Assert.Equal("Creator/Foo (2)", dupA.ProposedPath);
        Assert.Equal("Creator/Foo (3)", dupB.ProposedPath);
    }

    [Fact]
    public void Disambiguate_NoExactIdentifierMatch_LowestIdentifierStaysBareRestNumbered()
    {
        // Neither row's Identifier equals "Foo" - both copies were manually renamed
        // away from Penumbra's default, so there's no "original" signal to key off.
        var rowZeta = MakeRow("Zeta", "Foo", "Creator/Foo");
        var rowAlpha = MakeRow("Alpha", "Foo", "Creator/Foo");

        CollisionDisambiguator.Disambiguate([rowZeta, rowAlpha]);

        Assert.Equal("Creator/Foo", rowAlpha.ProposedPath);
        Assert.Equal("Creator/Foo (2)", rowZeta.ProposedPath);
    }

    [Fact]
    public void Disambiguate_NonCollidingGroups_AreLeftUntouched()
    {
        var apple = MakeRow("a", "Apple", "Creator/Apple");
        var banana = MakeRow("b", "Banana", "Creator/Banana");

        CollisionDisambiguator.Disambiguate([apple, banana]);

        Assert.Equal("Creator/Apple", apple.ProposedPath);
        Assert.Equal("Creator/Banana", banana.ProposedPath);
    }

    [Fact]
    public void Disambiguate_ExistingSuffixAlreadyTaken_SkipsToNextFreeSuffix()
    {
        var canonical = MakeRow("Foo", "Foo", "Creator/Foo");
        var duplicate = MakeRow("Foo_2", "Foo", "Creator/Foo");
        var unrelated = MakeRow("c", "Foo (2)", "Creator/Foo (2)");

        CollisionDisambiguator.Disambiguate([canonical, duplicate, unrelated]);

        Assert.Equal("Creator/Foo", canonical.ProposedPath);
        Assert.Equal("Creator/Foo (2)", unrelated.ProposedPath);
        Assert.Equal("Creator/Foo (3)", duplicate.ProposedPath);
    }

    [Fact]
    public void Disambiguate_ExistingSuffixCaseInsensitive_StillSkipped()
    {
        var canonical = MakeRow("Foo", "Foo", "Creator/Foo");
        var duplicate = MakeRow("Foo_2", "Foo", "Creator/Foo");
        var unrelated = MakeRow("c", "FOO (2)", "Creator/FOO (2)");

        CollisionDisambiguator.Disambiguate([canonical, duplicate, unrelated]);

        Assert.Equal("Creator/Foo (3)", duplicate.ProposedPath);
    }

    [Fact]
    public void Disambiguate_MultipleOccupiedSuffixes_SkipsAllOfThem()
    {
        var canonical = MakeRow("Foo", "Foo", "Creator/Foo");
        var dupA = MakeRow("Foo_2", "Foo", "Creator/Foo");
        var dupB = MakeRow("Foo_3", "Foo", "Creator/Foo");
        var occupiedTwo = MakeRow("c", "Foo (2)", "Creator/Foo (2)");
        var occupiedThree = MakeRow("d", "Foo (3)", "Creator/Foo (3)");

        CollisionDisambiguator.Disambiguate([canonical, dupA, dupB, occupiedTwo, occupiedThree]);

        var dupPaths = new[] { dupA.ProposedPath, dupB.ProposedPath };
        Assert.Contains("Creator/Foo (4)", dupPaths);
        Assert.Contains("Creator/Foo (5)", dupPaths);
    }

    [Fact]
    public void Disambiguate_CrossGroupCollision_ReservesAcrossEntireInput()
    {
        // The scenario raised in review: a naive per-group suffix would collide with
        // an independently-named mod's own bare path (also named "Foo (2)").
        var canonical = MakeRow("Foo", "Foo", "Creator/Foo");
        var duplicate = MakeRow("Foo_2", "Foo", "Creator/Foo");
        var independentlyNamed = MakeRow("c", "Foo (2)", "Creator/Foo (2)");

        CollisionDisambiguator.Disambiguate([canonical, duplicate, independentlyNamed]);

        Assert.Equal("Creator/Foo (2)", independentlyNamed.ProposedPath);
        Assert.Equal("Creator/Foo (3)", duplicate.ProposedPath);
        Assert.NotEqual(independentlyNamed.ProposedPath, duplicate.ProposedPath);
    }

    [Fact]
    public void Disambiguate_CalledTwice_IsIdempotent()
    {
        var canonical = MakeRow("Foo", "Foo", "Creator/Foo");
        var duplicate = MakeRow("Foo_2", "Foo", "Creator/Foo");
        var rows = new[] { canonical, duplicate };

        CollisionDisambiguator.Disambiguate(rows);
        CollisionDisambiguator.Disambiguate(rows);

        Assert.Equal("Creator/Foo", canonical.ProposedPath);
        Assert.Equal("Creator/Foo (2)", duplicate.ProposedPath);
    }

    [Fact]
    public void Disambiguate_LazyEnumerableInput_EnumeratedExactlyOnce()
    {
        var canonical = MakeRow("Foo", "Foo", "Creator/Foo");
        var duplicate = MakeRow("Foo_2", "Foo", "Creator/Foo");
        var guarded = new SingleEnumerationGuard([canonical, duplicate]);

        CollisionDisambiguator.Disambiguate(guarded);

        Assert.Equal("Creator/Foo", canonical.ProposedPath);
        Assert.Equal("Creator/Foo (2)", duplicate.ProposedPath);
    }

    [Fact]
    public void Disambiguate_DuplicateIdentifiers_TerminatesWithUniquePaths()
    {
        // Invalid state - Penumbra guarantees Identifier uniqueness at install time.
        // The design only commits to termination and uniqueness here, not to which
        // row wins the canonical slot.
        var rowA = MakeRow("Foo", "Foo", "Creator/Foo");
        var rowB = MakeRow("Foo", "Foo", "Creator/Foo");

        CollisionDisambiguator.Disambiguate([rowA, rowB]);

        Assert.NotEqual(rowA.ProposedPath, rowB.ProposedPath);
    }

    [Fact] // Real 3-way duplicate-install case observed in-game; base name has its own
           // parenthetical ("(Expression)"), unrelated to the generated (2)/(3) suffix.
    public void Disambiguate_RealThreeWayDuplicateWithParenthesesInName_NumbersSequentially()
    {
        const string basePath = "Animation and VFX/Emotes/When that face 1 au ra keeps talking to you (Expression)";
        var canonical = MakeRow(
            "When that face 1 au ra keeps talking to you (Expression)",
            "When that face 1 au ra keeps talking to you (Expression)", basePath);
        var dupA = MakeRow("Foo_2", "When that face 1 au ra keeps talking to you (Expression)", basePath);
        var dupB = MakeRow("Foo_3", "When that face 1 au ra keeps talking to you (Expression)", basePath);

        CollisionDisambiguator.Disambiguate([canonical, dupA, dupB]);

        Assert.Equal(basePath, canonical.ProposedPath);
        Assert.Equal($"{basePath} (2)", dupA.ProposedPath);
        Assert.Equal($"{basePath} (3)", dupB.ProposedPath);
    }
}
