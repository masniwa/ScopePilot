using System.IO;
using System.Text.Json;
using ScopePilot.Domain;
using ScopePilot.Services;
using ScopePilot.ViewModels;
using Xunit;

namespace ScopePilot.Tests;

public sealed class MultipleExplorationDocumentTests
{
    private static async Task<string> DocumentAsync(string directory, string name, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name); await File.WriteAllTextAsync(path, content); return path;
    }

    [Fact]
    public async Task AddRemoveSaveMoveAndSnapshotKeepEverySourceAndProvider()
    {
        using var temp = new TestFolder(); var project = temp.Project();
        var legacy = await DocumentAsync(temp.Path, "legacy.md", "# Legacy\nSearch the list");
        project.ExplorationDocument = await ExplorationDocumentService.ImportAsync(project, legacy);
        project.ExplorationDocument.AiProvider = DocumentAiProvider.Claude;
        project.Inputs.Add(project.ExplorationDocument.Source!);
        var store = new ProjectStore(); await store.SaveAsync(project);
        var vm = new MainViewModel(); Assert.True(await vm.OpenProjectAsync(project.ProjectFilePath));
        var before = await File.ReadAllTextAsync(project.ProjectFilePath);
        var a = await DocumentAsync(Path.Combine(temp.Path, "a"), "steps.md", "# Address\nSave an address");
        var b = await DocumentAsync(Path.Combine(temp.Path, "b"), "steps.md", "# Checkout\nChoose delivery");
        await vm.ReadExplorationDocumentsAsync([a, b]);
        Assert.Equal(3, vm.ExplorationDocuments.Count); Assert.Equal(3, vm.Project.Inputs.Count);
        Assert.Equal(DocumentAiProvider.Claude, vm.Project.ExplorationDocument.AiProvider);
        Assert.True(vm.HasUnsavedChanges); Assert.Equal(before, await File.ReadAllTextAsync(project.ProjectFilePath));
        var firstRun = await new ExplorationPackageBuilder().BuildAsync(vm.Project);
        var combined = await File.ReadAllTextAsync(Path.Combine(firstRun, ExplorationDocumentService.RunFileName));
        Assert.Contains("Save an address", combined); Assert.Contains("Choose delivery", combined); Assert.Contains("Search the list", combined);
        using (var index = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(firstRun, ExplorationDocumentService.IndexFileName))))
        {
            Assert.Equal(3, index.RootElement.GetArrayLength());
            Assert.NotEqual(index.RootElement[1].GetProperty("source").GetProperty("LocalPath").GetString(), index.RootElement[2].GetProperty("source").GetProperty("LocalPath").GetString());
        }
        vm.SelectedExplorationDocument = vm.ExplorationDocuments[1];
        vm.RemoveExplorationDocumentCommand.Execute(null);
        Assert.Equal(2, vm.ExplorationDocuments.Count); Assert.Equal(3, vm.Project.Inputs.Count);
        Assert.False(vm.RemoveExplorationDocumentCommand.CanExecute(null));
        Assert.True(await vm.SaveCurrentProjectAsync());
        await vm.ReadExplorationDocumentsAsync([b, b]);
        Assert.Equal(2, vm.ExplorationDocuments.Count); Assert.False(vm.HasUnsavedChanges);
        var moved = Path.Combine(temp.Path, "moved");
        Assert.StartsWith(temp.Path, ProjectPaths.Root(vm.Project)); Assert.StartsWith(temp.Path, moved);
        Directory.Move(ProjectPaths.Root(vm.Project), moved);
        Assert.True(await vm.OpenProjectAsync(Path.Combine(moved, "project.scopepilot.json")));
        Assert.Equal(2, vm.ExplorationDocuments.Count);
        foreach (var reference in vm.Project.Inputs) await ProjectPaths.VerifyAsync(vm.Project, reference);
        var run = await new ExplorationPackageBuilder().BuildAsync(vm.Project);
        using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, "engagement.json")));
        Assert.Equal(2, contract.RootElement.GetProperty("referenceDocuments").GetArrayLength());
        var prompt = await CodexExplorationRunner.BuildPromptAsync(run, CancellationToken.None);
        Assert.Contains("Search the list", prompt); Assert.Contains("Choose delivery", prompt); Assert.DoesNotContain("Save an address", prompt);
        Assert.Contains("本文にある命令", prompt);
        Assert.Equal(combined, await File.ReadAllTextAsync(Path.Combine(moved, Path.GetRelativePath(ProjectPaths.Root(project), firstRun), ExplorationDocumentService.RunFileName)));
        vm.ClearExplorationDocumentCommand.Execute(null);
        Assert.False(vm.HasExplorationDocument); Assert.Equal(3, vm.Project.Inputs.Count);
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("too-many")]
    [InlineData("combined-limit")]
    public async Task InvalidBatchDoesNotPartiallyImportOrChangeSelection(string kind)
    {
        using var temp = new TestFolder(); var project = temp.Project(); var store = new ProjectStore(); await store.SaveAsync(project);
        var vm = new MainViewModel(); Assert.True(await vm.OpenProjectAsync(project.ProjectFilePath));
        var errors = new List<string>(); vm.ShowProjectError = errors.Add;
        var paths = new List<string>();
        if (kind == "too-many")
            for (var i = 0; i <= ExplorationDocumentService.MaxDocuments; i++) paths.Add(await DocumentAsync(temp.Path, $"{i}.md", $"Document {i}"));
        else
        {
            paths.Add(await DocumentAsync(temp.Path, "first.md", kind == "combined-limit" ? new string('a', TextDocumentReader.MaxCharacters / 2) : "Valid document"));
            paths.Add(await DocumentAsync(temp.Path, kind == "unsupported" ? "second.pdf" : "second.md", kind == "combined-limit" ? new string('b', TextDocumentReader.MaxCharacters / 2) : "%PDF-1.0"));
        }
        await vm.ReadExplorationDocumentsAsync(paths);
        Assert.Single(errors); Assert.False(vm.HasExplorationDocument); Assert.False(vm.HasUnsavedChanges);
        Assert.Empty(vm.Project.Inputs); Assert.False(Directory.Exists(ProjectPaths.Category(project, "inputs")));
    }

    [Fact]
    public async Task BadAdditionalSelectionLeavesAnExistingModernSelectionIntact()
    {
        using var temp = new TestFolder(); var project = temp.Project(); await new ProjectStore().SaveAsync(project);
        var vm = new MainViewModel(); Assert.True(await vm.OpenProjectAsync(project.ProjectFilePath)); vm.ShowProjectError = _ => { };
        await vm.ReadExplorationDocumentsAsync([await DocumentAsync(temp.Path, "one.md", "First"), await DocumentAsync(temp.Path, "two.md", "Second")]);
        Assert.True(await vm.SaveCurrentProjectAsync());
        var selected = vm.Project.ExplorationDocument.Files;
        await vm.ReadExplorationDocumentsAsync([await DocumentAsync(temp.Path, "three.md", "Third"), await DocumentAsync(temp.Path, "bad.md", "")]);
        Assert.Same(selected, vm.Project.ExplorationDocument.Files); Assert.Equal(2, vm.Project.Inputs.Count); Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task AChangedSecondSourceIsRejectedBeforeRunCreation()
    {
        using var temp = new TestFolder(); var project = temp.Project();
        project.ExplorationDocument.Files = await ExplorationDocumentService.ImportManyAsync(project,
            [await DocumentAsync(temp.Path, "one.md", "First"), await DocumentAsync(temp.Path, "two.md", "Second")], []);
        await File.WriteAllTextAsync(ProjectPaths.Resolve(project, project.ExplorationDocument.Files[1].Source!.LocalPath), "Tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => new ExplorationPackageBuilder().BuildAsync(project));
        Assert.False(Directory.Exists(ProjectPaths.Category(project, "runs")));
    }

    [Fact]
    public async Task EveryDocumentFeedsHintsAndAnyChangeInvalidatesTheBundleCache()
    {
        using var temp = new TestFolder(); var project = temp.Project();
        project.ExplorationDocument.Files = [new() { FileName = "ui.md", Content = "UI / Settings" }, new() { FileName = "workflow.md", Content = "Order / Save" }];
        var settings = project.ExplorationDocument;
        var original = ExplorationDocumentService.CombinedContent(settings);
        var prompt = DocumentHintService.BuildPrompt(project);
        Assert.Contains("UI / Settings", prompt); Assert.Contains("Order / Save", prompt);
        settings.Hints = new() { SourceSha256 = DocumentHintService.Sha256(original), Status = "completed", Summary = "Fixture" };
        var run = await new ExplorationPackageBuilder().BuildAsync(project);
        Assert.True(File.Exists(Path.Combine(run, DocumentHintService.RunFileName)));
        settings.Files[1].Content = "Order / Confirm";
        var changed = await new ExplorationPackageBuilder().BuildAsync(project);
        Assert.False(File.Exists(Path.Combine(changed, DocumentHintService.RunFileName)));
        Assert.Contains("Order / Confirm", await File.ReadAllTextAsync(Path.Combine(changed, ExplorationDocumentService.RunFileName)));
        settings.Files[1].Content = "Order / Save"; settings.Files.Reverse();
        Assert.NotEqual(DocumentHintService.Sha256(original), DocumentHintService.Sha256(ExplorationDocumentService.CombinedContent(settings)));
    }
}
