using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PayloadPanda.Models;
using PayloadPanda.Services;

namespace PayloadPanda.ViewModels;

/// <summary>One row of the collections tree: a collection, a folder or a request.</summary>
public partial class CollectionNodeViewModel : ObservableObject
{
    private CollectionNodeViewModel(CollectionModel collection, CollectionItem? item)
    {
        Collection = collection;
        Item = item;
        _name = item?.Name ?? collection.Name;
    }

    public CollectionModel Collection { get; }

    /// <summary>Null for the collection's own node.</summary>
    public CollectionItem? Item { get; }

    public ObservableCollection<CollectionNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private bool _isExpanded;

    public bool IsCollection => Item is null;
    public bool IsFolder => Item is { IsFolder: true };
    public bool IsRequest => Item is { IsFolder: false };
    public bool IsContainer => !IsRequest;
    public HttpMethodType Method => Item?.Request?.Method ?? HttpMethodType.GET;

    /// <summary>The list this node's children live in.</summary>
    public List<CollectionItem> ChildItems => Item?.Items ?? Collection.Items;

    public Guid NodeId => Item?.Id ?? Collection.Id;

    public static CollectionNodeViewModel Build(CollectionModel collection)
    {
        var root = new CollectionNodeViewModel(collection, null);
        AddChildren(root, collection.Items);
        return root;

        static void AddChildren(CollectionNodeViewModel parent, List<CollectionItem> items)
        {
            foreach (var item in items)
            {
                var node = new CollectionNodeViewModel(parent.Collection, item);
                parent.Children.Add(node);
                AddChildren(node, item.Items);
            }
        }
    }

    public IEnumerable<CollectionNodeViewModel> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var node in child.SelfAndDescendants())
                yield return node;
        }
    }
}

/// <summary>
/// The COLLECTIONS tab: the tree of collections and everything done to them. The operations
/// themselves (<c>…Async</c> methods) take no dialogs, so they can be driven without a UI;
/// the commands add the file pickers, name prompts and confirmations around them.
/// </summary>
public partial class CollectionsViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private readonly CollectionService _service;

    public CollectionsViewModel(MainViewModel owner, CollectionService service)
    {
        _owner = owner;
        _service = service;
    }

    public ObservableCollection<CollectionNodeViewModel> Roots { get; } = [];

    // False while collections are still loading at startup (tabs are restored before them).
    public bool IsLoaded { get; private set; }

    [ObservableProperty]
    private string _statusText = string.Empty;

    public CollectionModel? Find(Guid? id) =>
        id is { } value ? Roots.FirstOrDefault(r => r.Collection.Id == value)?.Collection : null;

    public async Task LoadFromDiskAsync()
    {
        try
        {
            foreach (var collection in await _service.LoadAllAsync())
                Roots.Add(CollectionNodeViewModel.Build(collection));
        }
        finally
        {
            IsLoaded = true;
        }
    }

    // ==================== Operations ====================

    /// <exception cref="FormatException">The text isn't a Postman collection.</exception>
    public async Task<PostmanImportResult?> ImportJsonAsync(string json)
    {
        var result = PostmanCollectionConverter.Import(json);
        return await AddCollectionAsync(result.Collection) ? result : null;
    }

    public async Task<bool> AddCollectionAsync(CollectionModel collection)
    {
        // Only list it once it's actually on disk.
        if (!await TrySaveAsync(collection))
            return false;

        var node = CollectionNodeViewModel.Build(collection);
        node.IsExpanded = true;
        Roots.Add(node);
        return true;
    }

    public async Task<bool> AddFolderAsync(CollectionNodeViewModel parent, string name)
    {
        var folder = new CollectionItem { Name = name };
        parent.ChildItems.Add(folder);
        if (!await TrySaveAsync(parent.Collection))
        {
            parent.ChildItems.Remove(folder);
            return false;
        }

        Rebuild(parent.Collection, expand: [parent.NodeId]);
        return true;
    }

    /// <summary>Adds the tab's request to a collection or folder and links the tab to the new item.</summary>
    public async Task<bool> AddRequestFromTabAsync(CollectionNodeViewModel parent, RequestWorkspaceViewModel tab)
    {
        var item = new CollectionItem { Name = tab.Title, Request = tab.BuildRequestModel() };
        parent.ChildItems.Add(item);
        if (!await TrySaveAsync(parent.Collection))
        {
            parent.ChildItems.Remove(item);
            return false;
        }

        tab.LinkToCollectionItem(parent.Collection, item);
        tab.StatusText = $"Saved \"{item.Name}\" in {parent.Collection.Name}";
        Rebuild(parent.Collection, expand: [parent.NodeId]);
        return true;
    }

    /// <returns>
    /// True when the save was handled here (done, or failed and reported on the tab); false when
    /// the tab's collection request no longer exists, so the caller saves it some other way.
    /// </returns>
    public async Task<bool> SaveTabToItemAsync(RequestWorkspaceViewModel tab)
    {
        if (tab.ActiveCollectionItemId is not { } itemId)
            return false;

        var collection = Find(tab.ActiveCollectionId);
        if (collection is null && !IsLoaded)
        {
            // The collection may simply not be loaded yet; saving elsewhere would fork the request.
            tab.StatusText = "Collections are still loading - try again in a moment";
            return true;
        }
        if (collection?.FindItem(itemId) is not { IsFolder: false } item)
            return false;

        var previous = item.Request;
        item.Request = tab.BuildRequestModel();
        if (!await TrySaveAsync(collection))
        {
            // Keep the collection matching what's on disk.
            item.Request = previous;
            tab.StatusText = $"Save failed: {StatusText}";
            return true;
        }

        tab.IsDirty = false;
        tab.StatusText = $"Saved \"{item.Name}\" in {collection.Name}";
        Rebuild(collection);
        return true;
    }

    public async Task<bool> RenameAsync(CollectionNodeViewModel node, string name)
    {
        var previous = node.Name;
        SetName(node, name);
        if (!await TrySaveAsync(node.Collection))
        {
            SetName(node, previous);
            return false;
        }

        foreach (var tab in _owner.Tabs)
        {
            if (node.Item is { } item)
                tab.RenameCollectionItemLink(item.Id, name);
            tab.RefreshVariableScope();
        }
        return true;

        static void SetName(CollectionNodeViewModel target, string value)
        {
            if (target.Item is { } item)
                item.Name = value;
            else
                target.Collection.Name = value;
            target.Name = value;
        }
    }

    public async Task<bool> DuplicateAsync(CollectionNodeViewModel node)
    {
        if (node.Item is not { } item)
        {
            var copy = node.Collection.Clone();
            copy.Id = Guid.NewGuid();
            copy.Name = node.Collection.Name + " (copy)";
            copy.CreatedAt = DateTime.Now;
            copy.Items = copy.Items.Select(i => i.CloneWithNewIds()).ToList();
            return await AddCollectionAsync(copy);
        }

        var container = node.Collection.FindContainer(item.Id);
        if (container is null)
            return false;

        var duplicate = item.CloneWithNewIds();
        duplicate.Name = item.Name + " (copy)";
        container.Insert(container.IndexOf(item) + 1, duplicate);
        if (!await TrySaveAsync(node.Collection))
        {
            container.Remove(duplicate);
            return false;
        }

        Rebuild(node.Collection);
        return true;
    }

    public async Task<bool> DeleteAsync(CollectionNodeViewModel node)
    {
        var collection = node.Collection;
        if (node.Item is not { } item)
        {
            try
            {
                _service.Delete(collection.Id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StatusText = $"Could not delete \"{collection.Name}\": {ex.Message}";
                return false;
            }

            Roots.Remove(node);
            foreach (var tab in _owner.Tabs)
                tab.ClearCollectionLink(collection.Id, itemIds: null);
            return true;
        }

        var container = collection.FindContainer(item.Id);
        if (container is null)
            return false;

        var index = container.IndexOf(item);
        container.RemoveAt(index);
        if (!await TrySaveAsync(collection))
        {
            container.Insert(index, item);
            return false;
        }

        var removedIds = node.SelfAndDescendants().Select(n => n.NodeId).ToHashSet();
        foreach (var tab in _owner.Tabs)
            tab.ClearCollectionLink(collection.Id, removedIds);
        Rebuild(collection);
        return true;
    }

    /// <summary>Takes over the name, description, variables and collection scripts from an edited copy.</summary>
    public async Task<bool> ApplyEditAsync(CollectionModel collection, CollectionModel edited)
    {
        var previous = collection.Clone();
        Copy(edited, collection);
        if (!await TrySaveAsync(collection))
        {
            Copy(previous, collection);
            return false;
        }

        if (Roots.FirstOrDefault(r => r.Collection == collection) is { } root)
            root.Name = collection.Name;
        foreach (var tab in _owner.Tabs)
            tab.RefreshVariableScope();
        return true;

        static void Copy(CollectionModel from, CollectionModel to)
        {
            to.Name = from.Name;
            to.Description = from.Description;
            to.Variables = from.Variables.Select(v => v.Clone()).ToList();
            to.PreRequestScript = from.PreRequestScript;
            to.TestScript = from.TestScript;
        }
    }

    /// <summary>Saves after a script changed variables; failures go to the log and the status line.</summary>
    public void SaveInBackground(CollectionModel collection) => _ = TrySaveAsync(collection);

    private async Task<bool> TrySaveAsync(CollectionModel collection)
    {
        try
        {
            // The live collection keeps changing on the UI thread; write a snapshot off it.
            collection.ModifiedAt = DateTime.Now;
            var snapshot = collection.Clone();
            await Task.Run(() => _service.SaveAsync(snapshot));
            collection.SecretsUnreadable = false;
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Save collection", ex);
            StatusText = $"Could not save \"{collection.Name}\": {ex.Message}";
            return false;
        }
    }

    // Replaces a collection's node after its structure changed, keeping what was expanded.
    private void Rebuild(CollectionModel collection, IEnumerable<Guid>? expand = null)
    {
        var index = Roots.ToList().FindIndex(r => r.Collection == collection);
        if (index < 0)
            return;

        var expanded = Roots[index].SelfAndDescendants().Where(n => n.IsExpanded).Select(n => n.NodeId).ToHashSet();
        if (expand is not null)
            expanded.UnionWith(expand);

        var rebuilt = CollectionNodeViewModel.Build(collection);
        foreach (var node in rebuilt.SelfAndDescendants())
            node.IsExpanded = expanded.Contains(node.NodeId);
        Roots[index] = rebuilt;
    }

    // ==================== Commands ====================

    [RelayCommand]
    private async Task ImportAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Postman collection",
            Filter = "Postman collection (*.json)|*.json|All Files (*.*)|*.*",
            DefaultExt = ".json"
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var result = await ImportJsonAsync(await File.ReadAllTextAsync(dialog.FileName));
            if (result is null)
                return;

            var requests = result.Collection.AllItems().Count(i => !i.IsFolder);
            StatusText = $"Imported \"{result.Collection.Name}\" ({requests} request{(requests == 1 ? "" : "s")})";
            if (result.Warnings.Count > 0)
            {
                var shown = result.Warnings.Take(15).ToList();
                if (result.Warnings.Count > shown.Count)
                    shown.Add($"… and {result.Warnings.Count - shown.Count} more");
                MessageBox.Show($"\"{result.Collection.Name}\" was imported, with these notes:\n\n{string.Join("\n", shown)}",
                    "Import Collection", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Could not import the collection: {ex.Message}", "Import Collection",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task NewCollectionAsync()
    {
        if (PromptForName("New Collection") is { } name)
            await AddCollectionAsync(new CollectionModel { Name = name });
    }

    [RelayCommand]
    private async Task ExportAsync(CollectionNodeViewModel? node)
    {
        if (node is null)
            return;

        var safeName = string.Join("_", node.Collection.Name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var dialog = new SaveFileDialog
        {
            Title = "Export as Postman collection",
            Filter = "Postman collection (*.json)|*.json|All Files (*.*)|*.*",
            DefaultExt = ".json",
            FileName = (safeName.Length > 0 ? safeName : "collection") + ".postman_collection.json"
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            await File.WriteAllTextAsync(dialog.FileName, PostmanCollectionConverter.Export(node.Collection));
            StatusText = $"Exported to {Path.GetFileName(dialog.FileName)} (variable values and credentials are in plain text)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task EditCollectionAsync(CollectionNodeViewModel? node)
    {
        if (node is null)
            return;

        var editCopy = node.Collection.Clone();
        var dialog = new Views.CollectionSettingsWindow(editCopy) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() == true)
            await ApplyEditAsync(node.Collection, editCopy);
    }

    [RelayCommand]
    private async Task NewFolderAsync(CollectionNodeViewModel? node)
    {
        if (node is { IsContainer: true } && PromptForName("New Folder") is { } name)
            await AddFolderAsync(node, name);
    }

    [RelayCommand]
    private async Task AddCurrentRequestAsync(CollectionNodeViewModel? node)
    {
        if (node is { IsContainer: true } && _owner.SelectedTab is { } tab)
            await AddRequestFromTabAsync(node, tab);
    }

    /// <summary>A click on a row: a request opens, a collection or folder expands or collapses.</summary>
    [RelayCommand]
    private void Activate(CollectionNodeViewModel? node)
    {
        if (node is null)
            return;

        if (node.Item is { IsFolder: false } item)
            _owner.OpenCollectionItem(node.Collection, item);
        else
            node.IsExpanded = !node.IsExpanded;
    }

    [RelayCommand]
    private async Task RenameNodeAsync(CollectionNodeViewModel? node)
    {
        if (node is not null && PromptForName(node.Name) is { } name)
            await RenameAsync(node, name);
    }

    [RelayCommand]
    private async Task DuplicateNodeAsync(CollectionNodeViewModel? node)
    {
        if (node is not null)
            await DuplicateAsync(node);
    }

    [RelayCommand]
    private async Task DeleteNodeAsync(CollectionNodeViewModel? node)
    {
        if (node is null)
            return;

        var what = node.IsCollection ? "the collection" : node.IsFolder ? "the folder" : "the request";
        var contents = node.IsRequest ? string.Empty : " and everything in it";
        var confirm = MessageBox.Show($"Delete {what} \"{node.Name}\"{contents}? This can't be undone.", "Delete",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm == MessageBoxResult.Yes)
            await DeleteAsync(node);
    }

    private static string? PromptForName(string current)
    {
        var dialog = new Views.RenameDialog(current) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.NewName) ? dialog.NewName : null;
    }
}
