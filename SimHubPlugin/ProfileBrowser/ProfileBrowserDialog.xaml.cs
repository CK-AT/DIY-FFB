using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Newtonsoft.Json;
using DiyFfb.Controls;
using DiyFfb.GraphEditor;
using DiyFfb.Helpers;

namespace DiyFfb.ProfileBrowser
{
    public partial class ProfileBrowserDialog : Window
    {
        // Apply-target card border: green = ready to apply, amber = no active vehicle.
        private static readonly Brush ApplyBorderReady =
            new SolidColorBrush(Color.FromRgb(0x3A, 0x6A, 0x3A));
        private static readonly Brush ApplyBorderUnavailable =
            new SolidColorBrush(Color.FromRgb(0x8A, 0x6A, 0x30));

        private const string AllGamesFilter = "<All Games>";
        private const string ImportsFilter = "<Imports>";

        // Public result properties
        public ProfileBrowserEntry SelectedEntry { get; private set; }
        public bool UseGraphOnly { get; private set; }
        public bool UseTuning { get; private set; }
        // Plan 17: when false, caller keeps the current vehicle's graph and
        // applies only the source profile's tuning. Set by "Use Tuning Only".
        public bool UseSourceGraph { get; private set; } = true;
        // Plan 36: additional per-profile state to transport onto the active vehicle.
        public bool UseFunctionTuning { get; private set; }
        public bool UseSimVarBindings { get; private set; }
        public bool UseActiveFunctions { get; private set; }

        // Data binding
        public ObservableCollection<ProfileBrowserEntry> Items { get; } = new ObservableCollection<ProfileBrowserEntry>();

        // All stored profiles (unfiltered)
        private List<ProfileBrowserEntry> _allStoredProfiles = new List<ProfileBrowserEntry>();

        // Staged imports (not yet saved to library)
        private List<ProfileBrowserEntry> _stagedImports = new List<ProfileBrowserEntry>();

        // Constructor params
        private readonly ProfileBrowserMode _mode;
        private readonly DiyFfbPlugin _plugin;
        private readonly string _currentGameId;
        private readonly string _currentCarId;
        // Friendly name of the active vehicle (active destination label / option).
        private readonly string _applyDestinationLabel;
        // Valid apply destinations: the active vehicle, plus (in Manage mode) every
        // stored vehicle profile — so a profile can be configured before it is active.
        private List<DestinationOption> _destinationOptions = new List<DestinationOption>();
        // Current search query; filters the active category by name/game/graph.
        private string _searchText = "";
        private bool _isInitialized;

        /// <summary>A vehicle profile the apply action can write to.</summary>
        private sealed class DestinationOption
        {
            public string Label { get; set; }
            public string GameId { get; set; }
            public string CarId { get; set; }
            public bool IsActive { get; set; }
        }

        public ProfileBrowserDialog(
            DiyFfbPlugin plugin,
            ProfileBrowserMode mode,
            string gameId = null,
            string carId = null)
        {
            _plugin = plugin;
            _mode = mode;
            _currentGameId = gameId;
            _currentCarId = carId;

            string carName = plugin?.GetActiveCarName();
            _applyDestinationLabel = string.IsNullOrWhiteSpace(carName) ? "current vehicle" : carName;

            InitializeComponent();
            SourceInitialized += (s, e) => DarkTitleBar.Enable(this);

            ListItems.ItemsSource = Items;
            ConfigureForMode();
            BuildDestinationOptions();
            InitDestinationUi();
            _isInitialized = true;
            LoadInitialTab();
        }

        private void ConfigureForMode()
        {
            switch (_mode)
            {
                case ProfileBrowserMode.NewVehicle:
                    TextTitle.Text = "Select Starting Point";
                    RadioTemplates.IsChecked = true;
                    ButtonExport.Visibility = Visibility.Collapsed;
                    break;
                case ProfileBrowserMode.ManageProfiles:
                    TextTitle.Text = "Profile Manager";
                    RadioMyVehicles.IsChecked = true;
                    break;
                case ProfileBrowserMode.CopyFromVehicle:
                    TextTitle.Text = "Copy From Vehicle";
                    RadioMyVehicles.IsChecked = true;
                    ButtonExport.Visibility = Visibility.Collapsed;
                    break;
            }

            UpdateButtonStates();
        }

        private void LoadInitialTab()
        {
            RefreshCurrentTab();
        }

        private void OnTabChanged(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized)
                return;
            RefreshCurrentTab();
        }

        // Loads the category matching the checked tab. Each loader sets the game-filter
        // visibility and applies the current search, so both tab and search route here.
        private void RefreshCurrentTab()
        {
            if (RadioCustom.IsChecked == true)
                LoadCustom();
            else if (RadioMyVehicles.IsChecked == true)
                LoadStoredProfiles();
            else
                LoadTemplates();
        }

        private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (!_isInitialized)
                return;
            _searchText = SearchBox.Text?.Trim() ?? "";
            RefreshCurrentTab();
        }

        private bool PassesSearch(ProfileBrowserEntry entry)
        {
            if (string.IsNullOrEmpty(_searchText))
                return true;

            bool Contains(string s) =>
                !string.IsNullOrEmpty(s) && s.IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0;

            return Contains(entry.Name) || Contains(entry.GameId) || Contains(entry.GraphName);
        }

        // Custom category: graph files the user copied into the managed library
        // (graphs/custom). Each is a graph-only source selectable like a template.
        private void LoadCustom()
        {
            PanelGameFilter.Visibility = Visibility.Collapsed;
            Items.Clear();
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var path in GraphPathUtil.EnumerateCustomLibrary(baseDir))
            {
                var entry = ProfileBrowserEntry.FromCustomFile(GraphPathUtil.MakeRelative(path, baseDir));
                if (PassesSearch(entry))
                    Items.Add(entry);
            }

            if (Items.Count > 0)
                ListItems.SelectedIndex = 0;

            UpdateApplyTarget();
            UpdateButtonStates();
        }

        private void LoadTemplates()
        {
            PanelGameFilter.Visibility = Visibility.Collapsed;
            Items.Clear();
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var templates = GraphTemplateRegistry.GetTemplates(_currentGameId, baseDir);

            foreach (var t in templates)
            {
                var entry = ProfileBrowserEntry.FromTemplate(t);
                if (PassesSearch(entry))
                    Items.Add(entry);
            }

            if (Items.Count > 0)
                ListItems.SelectedIndex = 0;

            UpdateApplyTarget();
            UpdateButtonStates();
        }

        private void LoadStoredProfiles()
        {
            PanelGameFilter.Visibility = Visibility.Visible;
            Items.Clear();
            _allStoredProfiles.Clear();

            var profiles = _plugin?.Settings?.AircraftFfbProfiles;
            if (profiles == null)
            {
                PopulateGameFilter();
                UpdateApplyTarget();
                UpdateButtonStates();
                return;
            }

            // Identify the currently active vehicle so its entry can be flagged.
            string currentKey = _plugin?.GetActiveProfileKey();

            // Build full list of profiles with game ID extracted
            foreach (var kvp in profiles)
            {
                string graphPath = kvp.Value?.GraphPath ?? "";
                var entry = ProfileBrowserEntry.FromProfile(kvp.Key, kvp.Value, graphPath);
                entry.GameId = ExtractGameId(kvp.Key);
                entry.IsCurrent = !string.IsNullOrEmpty(currentKey)
                    && string.Equals(kvp.Key, currentKey, StringComparison.Ordinal);
                _allStoredProfiles.Add(entry);
            }

            PopulateGameFilter();
            ApplyGameFilter();

            // Bring the active vehicle's profile into view (if it survived filtering).
            var current = Items.FirstOrDefault(e => e.IsCurrent);
            if (current != null)
            {
                ListItems.ScrollIntoView(current);
            }
        }

        private string ExtractGameId(string profileKey)
        {
            if (string.IsNullOrEmpty(profileKey))
                return null;

            int sep = profileKey.IndexOf("::");
            return sep > 0 ? profileKey.Substring(0, sep) : null;
        }

        private void PopulateGameFilter()
        {
            var previousSelection = ComboGameFilter.SelectedItem as string;
            ComboGameFilter.Items.Clear();
            ComboGameFilter.Items.Add(AllGamesFilter);

            // Add <Imports> option if there are staged imports
            if (_stagedImports.Count > 0)
            {
                ComboGameFilter.Items.Add(ImportsFilter);
            }

            // Get distinct game IDs, sorted
            var gameIds = _allStoredProfiles
                .Where(p => !string.IsNullOrEmpty(p.GameId))
                .Select(p => p.GameId)
                .Distinct()
                .OrderBy(g => g)
                .ToList();

            foreach (var gameId in gameIds)
            {
                ComboGameFilter.Items.Add(gameId);
            }

            // Select appropriate default
            if (previousSelection != null && ComboGameFilter.Items.Contains(previousSelection))
            {
                ComboGameFilter.SelectedItem = previousSelection;
            }
            else if (!string.IsNullOrEmpty(_currentGameId) && ComboGameFilter.Items.Contains(_currentGameId))
            {
                ComboGameFilter.SelectedItem = _currentGameId;
            }
            else
            {
                ComboGameFilter.SelectedItem = AllGamesFilter;
            }
        }

        private void ApplyGameFilter()
        {
            Items.Clear();
            string selectedGame = ComboGameFilter.SelectedItem as string;

            IEnumerable<ProfileBrowserEntry> filtered;
            if (selectedGame == ImportsFilter)
            {
                filtered = _stagedImports;
            }
            else if (!string.IsNullOrEmpty(selectedGame) && selectedGame != AllGamesFilter)
            {
                filtered = _allStoredProfiles.Where(p => p.GameId == selectedGame);
            }
            else
            {
                filtered = _allStoredProfiles;
            }

            foreach (var entry in filtered)
            {
                if (PassesSearch(entry))
                    Items.Add(entry);
            }

            if (Items.Count > 0)
                ListItems.SelectedIndex = 0;

            UpdateApplyTarget();
            UpdateButtonStates();
        }

        private void OnGameFilterChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!_isInitialized)
                return;

            ApplyGameFilter();
        }

        private void OnImportClick(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = "json",
                Title = "Import Profiles",
                Multiselect = true
            };

            if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0)
                return;

            var importedEntries = new List<ProfileBrowserEntry>();
            var failedFiles = new List<string>();

            foreach (string filePath in dialog.FileNames)
            {
                try
                {
                    var entry = ImportSingleFile(filePath);
                    if (entry != null)
                    {
                        importedEntries.Add(entry);
                    }
                    else
                    {
                        failedFiles.Add(Path.GetFileName(filePath));
                    }
                }
                catch (Exception ex)
                {
                    failedFiles.Add($"{Path.GetFileName(filePath)}: {ex.Message}");
                }
            }

            if (importedEntries.Count > 0)
            {
                // Add to staged imports (don't replace existing staged imports)
                _stagedImports.AddRange(importedEntries);

                // Ensure My Vehicles tab is active
                RadioMyVehicles.IsChecked = true;

                // Rebuild filter to include <Imports>
                PopulateGameFilter();

                // Switch to Imports filter
                ComboGameFilter.SelectedItem = ImportsFilter;
            }

            if (failedFiles.Count > 0)
            {
                string message = failedFiles.Count == 1
                    ? $"Failed to import: {failedFiles[0]}"
                    : $"Failed to import {failedFiles.Count} file(s):\n\n{string.Join("\n", failedFiles.Take(5))}" +
                      (failedFiles.Count > 5 ? $"\n...and {failedFiles.Count - 5} more" : "");

                ThemedMessageBox.Show(this, message, "Import Warning",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnBrowseCustomGraphClick(object sender, RoutedEventArgs e)
        {
            var picker = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Graph JSON (*.json)|*.json|All files (*.*)|*.*",
                DefaultExt = "json",
                Title = "Select Custom Graph"
            };
            if (picker.ShowDialog() != true || string.IsNullOrEmpty(picker.FileName))
                return;

            string picked = picker.FileName;

            // Validate before assigning so a broken file can't be attached.
            try
            {
                string json = File.ReadAllText(picked);
                GraphSerializer.Deserialize(json, out var validation);
                if (validation == null || !validation.IsValid)
                {
                    string errors = validation?.Errors != null && validation.Errors.Count > 0
                        ? string.Join("\n", validation.Errors.Take(8))
                        : "Unknown error.";
                    ThemedMessageBox.Show(this,
                        $"This file is not a valid graph:\n\n{errors}",
                        "Invalid Graph", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            catch (Exception ex)
            {
                ThemedMessageBox.Show(this,
                    $"Could not read graph file:\n\n{ex.Message}",
                    "Invalid Graph", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Ask how to store it: reference in place vs copy into the library.
            var modeDialog = new GraphStorageModeDialog { Owner = this };
            modeDialog.ShowDialog();
            if (modeDialog.Result == GraphStorageMode.Cancel)
                return;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string resolved;
            try
            {
                resolved = modeDialog.Result == GraphStorageMode.Copy
                    ? GraphPathUtil.CopyIntoCustomLibrary(picked, baseDir)
                    : picked;
            }
            catch (Exception ex)
            {
                ThemedMessageBox.Show(this,
                    $"Could not copy the graph into the library:\n\n{ex.Message}",
                    "Copy Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            string stored = GraphPathUtil.MakeRelative(resolved, baseDir);

            // Assign to the current vehicle (graph only, no tuning) and close.
            SelectedEntry = ProfileBrowserEntry.FromCustomFile(stored);
            UseSourceGraph = true;
            UseGraphOnly = true;
            UseTuning = false;
            DialogResult = true;
        }

        private ProfileBrowserEntry ImportSingleFile(string filePath)
        {
            string json = File.ReadAllText(filePath);

            // Try new ExportedProfile format first
            var exported = JsonConvert.DeserializeObject<DiyFfbPluginSettings.ExportedProfile>(json);
            if (exported?.Profile != null)
            {
                return ProfileBrowserEntry.FromImportedFile(exported);
            }

            // Try legacy AircraftFfbProfile format
            var legacyProfile = JsonConvert.DeserializeObject<DiyFfbPluginSettings.AircraftFfbProfile>(json);
            if (legacyProfile != null)
            {
                var legacyExported = new DiyFfbPluginSettings.ExportedProfile
                {
                    Version = 1,
                    ProfileKey = Path.GetFileNameWithoutExtension(filePath),
                    Profile = legacyProfile
                };
                return ProfileBrowserEntry.FromImportedFile(legacyExported);
            }

            return null;
        }

        private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateApplyTarget();
            UpdateButtonStates();
        }

        // Populates the pinned apply-target card. Active only when exactly one
        // profile is selected (single-item "apply"); collapses to a hint otherwise
        // (zero, or many = a multi-item "manage" selection for Delete/Export).
        private void UpdateApplyTarget()
        {
            // No valid destination (no active vehicle and nothing to configure) → apply
            // is impossible; show the notice and leave the list/Delete/Export usable.
            if (_destinationOptions.Count == 0)
            {
                PanelApplyActive.Visibility = Visibility.Collapsed;
                TextApplyHint.Visibility = Visibility.Collapsed;
                PanelApplyNoVehicle.Visibility = Visibility.Visible;
                BorderApplyTarget.BorderBrush = ApplyBorderUnavailable;
                return;
            }

            PanelApplyNoVehicle.Visibility = Visibility.Collapsed;
            BorderApplyTarget.BorderBrush = ApplyBorderReady;

            var selected = ListItems.SelectedItems.Cast<ProfileBrowserEntry>().ToList();
            if (selected.Count == 1)
            {
                var entry = selected[0];
                TextApplyName.Text = entry.Name;

                string source = SourceLabel(entry.Source);
                string graph = string.IsNullOrEmpty(entry.GraphName) ? "no graph" : entry.GraphName;
                string tuning = entry.Source == ProfileEntrySource.Template
                    ? "template"
                    : entry.HasTuning ? $"{entry.TunedParamCount} param(s)" : "no tuning";
                TextApplyInfo.Text = $"{source} · {graph} · {tuning}";
                TextApplyDestination.Text = "→ " + _applyDestinationLabel;

                // Resolve the source graph; tooltip = full path, warn if the file is
                // gone (a referenced graph moved/deleted → the profile would be broken).
                string resolvedGraph = ResolveGraphForExistence(entry);
                TextApplyInfo.ToolTip = string.IsNullOrEmpty(resolvedGraph) ? null : resolvedGraph;
                bool graphMissing = !string.IsNullOrEmpty(entry.GraphPath)
                    && (string.IsNullOrEmpty(resolvedGraph) || !File.Exists(resolvedGraph));
                TextGraphMissing.Visibility = graphMissing ? Visibility.Visible : Visibility.Collapsed;
                if (graphMissing)
                    TextGraphMissing.Text = $"⚠ Graph file not found: {entry.GraphName}";

                // Toggle availability follows what the source offers; a template
                // has a graph but no tuning, a graph-less profile only tuning.
                bool hasGraph = !string.IsNullOrEmpty(entry.GraphPath);
                CheckUseGraph.IsEnabled = hasGraph;
                CheckUseGraph.IsChecked = hasGraph;
                CheckCopyTuning.IsEnabled = entry.HasTuning;
                CheckCopyTuning.IsChecked = entry.HasTuning;

                // Plan 36: the extra transportable state. Each toggle is available only
                // when the source carries that data; unchecked by default so a plain
                // "apply" stays graph+tuning as before (opt-in for the rest).
                CheckCopyFunctionTuning.IsEnabled = entry.HasFunctionTuning;
                CheckCopyFunctionTuning.IsChecked = false;
                CheckCopySimVarBindings.IsEnabled = entry.HasSimVarBindings;
                CheckCopySimVarBindings.IsChecked = false;
                CheckCopyActiveFunctions.IsEnabled = entry.HasActiveFunctions;
                CheckCopyActiveFunctions.IsChecked = false;

                UpdateGraphMismatchNote(entry);

                PanelApplyActive.Visibility = Visibility.Visible;
                TextApplyHint.Visibility = Visibility.Collapsed;
                UpdateApplyEnabled();
            }
            else
            {
                PanelApplyActive.Visibility = Visibility.Collapsed;
                TextApplyHint.Visibility = Visibility.Visible;
                TextApplyHint.Text = selected.Count == 0
                    ? "Select a profile to apply, or profiles to delete / export."
                    : $"{selected.Count} selected — use Delete or Export below, or select one to apply.";
            }
        }

        private void UpdateApplyEnabled()
        {
            bool anyToggle = CheckUseGraph.IsChecked == true
                || CheckCopyTuning.IsChecked == true
                || CheckCopyFunctionTuning.IsChecked == true
                || CheckCopySimVarBindings.IsChecked == true
                || CheckCopyActiveFunctions.IsChecked == true;
            bool sameVehicle = IsSourceSameAsDestination();
            TextApplyWarning.Visibility = sameVehicle ? Visibility.Visible : Visibility.Collapsed;
            ButtonApply.IsEnabled = anyToggle && !sameVehicle;
        }

        private void OnApplyToggleChanged(object sender, RoutedEventArgs e)
        {
            UpdateApplyEnabled();

            // The mismatch note only matters for the graph-scoped copies (function /
            // sim-var / active functions); refresh it as those toggles change.
            var entry = ListItems.SelectedItems.Count == 1
                ? ListItems.SelectedItems[0] as ProfileBrowserEntry
                : null;
            if (entry != null)
                UpdateGraphMismatchNote(entry);
        }

        // Plan 36: cross-graph copies are inert-not-harmful (orphan function ids /
        // aliases are ignored at scan time), but silently doing so is confusing. When a
        // graph-scoped copy is selected and the source graph differs from the graph the
        // destination will end up on, surface a non-blocking heads-up. Fail-open: if the
        // graphs can't be resolved, show nothing rather than a false alarm.
        private void UpdateGraphMismatchNote(ProfileBrowserEntry entry)
        {
            bool copiesGraphScoped = CheckCopyFunctionTuning.IsChecked == true
                || CheckCopySimVarBindings.IsChecked == true
                || CheckCopyActiveFunctions.IsChecked == true;
            if (!copiesGraphScoped)
            {
                TextGraphMismatch.Visibility = Visibility.Collapsed;
                return;
            }

            string sourceGraph = ResolveGraphForExistence(entry);

            // The destination lands on the source's graph when "Use graph" is on;
            // otherwise it keeps its own current graph.
            string destGraph = null;
            if (CheckUseGraph.IsChecked == true)
            {
                destGraph = sourceGraph;
            }
            else
            {
                var dest = CurrentDestination();
                if (dest != null)
                    destGraph = _plugin?.GetVehicleGraphPath(dest.GameId, dest.CarId);
            }

            string srcName = string.IsNullOrEmpty(sourceGraph) ? "" : Path.GetFileName(sourceGraph);
            string dstName = string.IsNullOrEmpty(destGraph) ? "" : Path.GetFileName(destGraph);

            // Can't determine either side → don't guess.
            if (srcName.Length == 0 || dstName.Length == 0)
            {
                TextGraphMismatch.Visibility = Visibility.Collapsed;
                return;
            }

            if (string.Equals(srcName, dstName, StringComparison.OrdinalIgnoreCase))
            {
                TextGraphMismatch.Visibility = Visibility.Collapsed;
                return;
            }

            TextGraphMismatch.Text =
                $"⚠ Source graph ({srcName}) differs from the destination graph ({dstName}). "
                + "Copied function / binding / active-function settings apply only where the destination graph uses the same functions and aliases.";
            TextGraphMismatch.Visibility = Visibility.Visible;
        }

        private void OnDestinationChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // Changing the destination can make it match (or stop matching) the source.
            UpdateApplyEnabled();
        }

        // True when the selected source is a stored vehicle profile whose vehicle is the
        // chosen destination — applying a profile onto itself is a no-op.
        private bool IsSourceSameAsDestination()
        {
            var entry = ListItems.SelectedItems.Count == 1
                ? ListItems.SelectedItems[0] as ProfileBrowserEntry
                : null;
            if (entry == null || entry.Source != ProfileEntrySource.StoredProfile)
                return false;

            var dest = CurrentDestination();
            if (dest == null)
                return false;

            SplitKey(entry.ProfileKey, out string g, out string c);
            return string.Equals(g ?? "", dest.GameId ?? "") && string.Equals(c ?? "", dest.CarId ?? "");
        }

        private static string SourceLabel(ProfileEntrySource source)
        {
            switch (source)
            {
                case ProfileEntrySource.Template: return "Template";
                case ProfileEntrySource.StoredProfile: return "My Vehicle";
                case ProfileEntrySource.ImportedFile: return "Imported";
                case ProfileEntrySource.CustomFile: return "Custom graph";
                default: return "";
            }
        }

        private void UpdateButtonStates()
        {
            var selectedItems = ListItems.SelectedItems.Cast<ProfileBrowserEntry>().ToList();

            bool hasAnyStaged = selectedItems.Any(e => e.Source == ProfileEntrySource.ImportedFile);
            bool hasAnyDeletable = selectedItems.Any(e => e.CanDelete);
            bool hasAnyExportable = selectedItems.Any(e => e.CanExport);

            // Save to Library: visible when viewing imports, enabled when staged items selected
            bool showingImports = (ComboGameFilter.SelectedItem as string) == ImportsFilter;
            ButtonSaveToLibrary.Visibility = showingImports ? Visibility.Visible : Visibility.Collapsed;
            ButtonSaveToLibrary.IsEnabled = hasAnyStaged;

            // Delete: visible on My Vehicles tab (not Templates), enabled when deletable items selected
            bool showingMyVehicles = RadioMyVehicles.IsChecked == true;
            ButtonDeleteSelected.Visibility = showingMyVehicles ? Visibility.Visible : Visibility.Collapsed;
            ButtonDeleteSelected.IsEnabled = hasAnyDeletable;

            // Export: any number of exportable items (batch-capable, like Delete).
            ButtonExport.IsEnabled = hasAnyExportable;

            // Note: the single-item "Apply" action lives in the pinned target card
            // (see UpdateApplyTarget); it is not a button in this bar.
        }

        private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ApplySelected();
        }

        private void OnApplyClick(object sender, RoutedEventArgs e)
        {
            ApplySelected();
        }

        // Applies the single selected profile to the current vehicle per the
        // Use graph / Copy tuning toggles. No-op unless exactly one is selected
        // and at least one toggle is on. Sets the result flags consumed by
        // ApplyProfileFromBrowser / PromptForGraphTemplate and closes the dialog.
        private void ApplySelected()
        {
            var dest = CurrentDestination();
            if (dest == null)
                return;

            var selected = ListItems.SelectedItems.Cast<ProfileBrowserEntry>().ToList();
            if (selected.Count != 1)
                return;

            bool useGraph = CheckUseGraph.IsChecked == true;
            bool useTuning = CheckCopyTuning.IsChecked == true;
            // Plan 36: extra opt-in copies (enabled only when the source has that data).
            bool copyFunctionTuning = CheckCopyFunctionTuning.IsChecked == true;
            bool copySimVarBindings = CheckCopySimVarBindings.IsChecked == true;
            bool copyActiveFunctions = CheckCopyActiveFunctions.IsChecked == true;
            if (!useGraph && !useTuning && !copyFunctionTuning && !copySimVarBindings && !copyActiveFunctions)
                return;

            // Applying a stored profile onto its own vehicle is a no-op (also guards
            // double-click, which bypasses the disabled Apply button).
            if (IsSourceSameAsDestination())
                return;

            var entry = selected[0];
            if (dest.IsActive)
            {
                // Active vehicle: return to the caller, which applies and reloads the
                // runtime graph (ApplyProfileFromBrowser / PromptForGraphTemplate).
                SelectedEntry = entry;
                UseSourceGraph = useGraph;
                UseTuning = useTuning;
                UseGraphOnly = useGraph && !useTuning
                    && !copyFunctionTuning && !copySimVarBindings && !copyActiveFunctions;
                UseFunctionTuning = copyFunctionTuning;
                UseSimVarBindings = copySimVarBindings;
                UseActiveFunctions = copyActiveFunctions;
                DialogResult = true;
                return;
            }

            // Non-active destination: apply directly (persist only). Leave SelectedEntry
            // null so the caller does not also apply to the active vehicle.
            string graphPath = useGraph ? ResolveEntryGraphPath(entry) : null;
            _plugin?.ApplyProfileToVehicle(dest.GameId, dest.CarId, graphPath, entry.Profile, useTuning,
                copyFunctionTuning, copySimVarBindings, copyActiveFunctions);
            ThemedMessageBox.Show(this,
                $"Applied to '{dest.Label}'.\n\nIt takes effect when that vehicle is next active.",
                "Applied", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }

        // Builds the set of vehicle profiles the apply action may target: the active
        // vehicle (if any), plus — in Manage mode — every stored profile, so a profile
        // can be configured before it becomes active.
        private void BuildDestinationOptions()
        {
            _destinationOptions = new List<DestinationOption>();

            string activeGame = _plugin?.GetActiveGameId();
            string activeCar = _plugin?.GetActiveCarId();
            bool hasActive = !string.IsNullOrWhiteSpace(activeCar);
            if (hasActive)
            {
                _destinationOptions.Add(new DestinationOption
                {
                    Label = _applyDestinationLabel,
                    GameId = activeGame,
                    CarId = activeCar,
                    IsActive = true
                });
            }

            if (_mode == ProfileBrowserMode.ManageProfiles && _plugin?.Settings?.AircraftFfbProfiles != null)
            {
                foreach (var key in _plugin.Settings.AircraftFfbProfiles.Keys.OrderBy(k => k))
                {
                    SplitKey(key, out string g, out string c);
                    if (string.IsNullOrEmpty(c))
                        continue;
                    // Skip the active vehicle — already added as the active option.
                    if (hasActive && string.Equals(g ?? "", activeGame ?? "") && string.Equals(c, activeCar))
                        continue;
                    _destinationOptions.Add(new DestinationOption
                    {
                        Label = FormatKeyLabel(key),
                        GameId = g,
                        CarId = c,
                        IsActive = false
                    });
                }
            }
        }

        // Shows the destination combo when there is a real choice (Manage mode with
        // more than one option); otherwise shows the single destination as static text.
        private void InitDestinationUi()
        {
            if (_destinationOptions.Count == 0)
                return; // amber "nothing to apply to" state handles this

            bool useCombo = _mode == ProfileBrowserMode.ManageProfiles && _destinationOptions.Count > 1;
            if (useCombo)
            {
                ComboDestination.ItemsSource = _destinationOptions;
                ComboDestination.SelectedItem = _destinationOptions.FirstOrDefault(o => o.IsActive)
                    ?? _destinationOptions[0];
                PanelDestinationCombo.Visibility = Visibility.Visible;
                TextApplyDestination.Visibility = Visibility.Collapsed;
            }
            else
            {
                PanelDestinationCombo.Visibility = Visibility.Collapsed;
                TextApplyDestination.Visibility = Visibility.Visible;
                TextApplyDestination.Text = "→ " + _destinationOptions[0].Label;
            }
        }

        private DestinationOption CurrentDestination()
        {
            if (PanelDestinationCombo.Visibility == Visibility.Visible)
                return ComboDestination.SelectedItem as DestinationOption;
            return _destinationOptions.FirstOrDefault();
        }

        private static string ResolveEntryGraphPath(ProfileBrowserEntry entry)
        {
            if (entry.TemplateEntry != null)
                return GraphTemplateRegistry.ResolveTemplatePath(
                    entry.TemplateEntry.TemplatePath, AppDomain.CurrentDomain.BaseDirectory);
            return entry.GraphPath;
        }

        // Absolute path used only to test whether the source graph file exists.
        // Templates resolve via the registry; everything else against the base dir.
        private static string ResolveGraphForExistence(ProfileBrowserEntry entry)
        {
            if (string.IsNullOrEmpty(entry.GraphPath))
                return null;
            if (entry.TemplateEntry != null)
                return GraphTemplateRegistry.ResolveTemplatePath(
                    entry.TemplateEntry.TemplatePath, AppDomain.CurrentDomain.BaseDirectory);
            return DiyFfbPlugin.ResolveGraphFilePath(entry.GraphPath);
        }

        private static void SplitKey(string key, out string gameId, out string carId)
        {
            int sep = key?.IndexOf("::") ?? -1;
            if (sep > 0)
            {
                gameId = key.Substring(0, sep);
                carId = key.Substring(sep + 2);
            }
            else
            {
                gameId = null;
                carId = key; // legacy bare carId
            }
        }

        private static string FormatKeyLabel(string key)
        {
            SplitKey(key, out string g, out string c);
            string car = (c ?? "").Replace('_', ' ');
            return string.IsNullOrEmpty(g) ? car : $"{car} ({g})";
        }

        private void OnListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && ButtonDeleteSelected.IsEnabled &&
                ButtonDeleteSelected.Visibility == Visibility.Visible)
            {
                OnDeleteSelectedClick(sender, e);
                e.Handled = true;
            }
        }

        private void OnDeleteSelectedClick(object sender, RoutedEventArgs e)
        {
            var selectedItems = ListItems.SelectedItems
                .Cast<ProfileBrowserEntry>()
                .Where(entry => entry.CanDelete)
                .ToList();

            if (selectedItems.Count == 0)
                return;

            // Check if any are stored profiles (require confirmation)
            var storedProfiles = selectedItems.Where(x => x.Source == ProfileEntrySource.StoredProfile).ToList();
            if (storedProfiles.Count > 0)
            {
                string message = storedProfiles.Count == 1
                    ? $"Delete profile for '{storedProfiles[0].Name}'?\n\nThis will remove all saved tuning for this vehicle."
                    : $"Delete {storedProfiles.Count} profiles?\n\nThis will remove all saved tuning for these vehicles.";

                var result = ThemedMessageBox.Show(this, message, "Confirm Delete",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                    return;
            }

            // Delete all selected items
            foreach (var entry in selectedItems)
            {
                if (entry.Source == ProfileEntrySource.StoredProfile)
                {
                    _plugin?.Settings?.AircraftFfbProfiles?.Remove(entry.ProfileKey);
                    _allStoredProfiles.Remove(entry);
                }
                else if (entry.Source == ProfileEntrySource.ImportedFile)
                {
                    _stagedImports.Remove(entry);
                }
                Items.Remove(entry);
            }

            // Refresh filter if needed
            if (_stagedImports.Count == 0 && (ComboGameFilter.SelectedItem as string) == ImportsFilter)
            {
                PopulateGameFilter();
                ComboGameFilter.SelectedItem = AllGamesFilter;
            }

            UpdateApplyTarget();
            UpdateButtonStates();
        }

        private void OnExportClick(object sender, RoutedEventArgs e)
        {
            var items = ListItems.SelectedItems.Cast<ProfileBrowserEntry>()
                .Where(x => x.CanExport)
                .ToList();
            if (items.Count == 0)
                return;

            if (items.Count == 1)
            {
                ExportSingle(items[0]);
                return;
            }

            // Multiple: pick one destination folder, write one file per profile.
            using (var folder = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Export selected profiles to folder"
            })
            {
                if (folder.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return;

                int exported = 0;
                var failed = new List<string>();
                foreach (var entry in items)
                {
                    try
                    {
                        string path = Path.Combine(folder.SelectedPath, ExportFileName(entry));
                        File.WriteAllText(path, SerializeExport(entry));
                        exported++;
                    }
                    catch (Exception ex)
                    {
                        failed.Add($"{entry.Name}: {ex.Message}");
                    }
                }

                string message = $"Exported {exported} profile(s) to:\n{folder.SelectedPath}";
                if (failed.Count > 0)
                    message += $"\n\nFailed:\n{string.Join("\n", failed.Take(5))}";
                ThemedMessageBox.Show(this, message,
                    failed.Count > 0 ? "Export Warning" : "Export Complete",
                    MessageBoxButton.OK,
                    failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
        }

        private void ExportSingle(ProfileBrowserEntry entry)
        {
            var saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json",
                DefaultExt = "json",
                FileName = ExportFileName(entry),
                Title = "Export Profile"
            };

            if (saveDialog.ShowDialog() != true)
                return;

            try
            {
                File.WriteAllText(saveDialog.FileName, SerializeExport(entry));
                ThemedMessageBox.Show(this, $"Profile exported to:\n{saveDialog.FileName}", "Export Complete",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ThemedMessageBox.Show(this, $"Error exporting profile: {ex.Message}", "Export Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string ExportFileName(ProfileBrowserEntry entry)
        {
            string name = (entry.Name ?? "profile").Replace(' ', '_').Replace(':', '_');
            return $"{name}_ffb.json";
        }

        private static string SerializeExport(ProfileBrowserEntry entry)
        {
            var exported = new DiyFfbPluginSettings.ExportedProfile
            {
                Version = 1,
                ProfileKey = entry.ProfileKey,
                GraphPath = entry.GraphPath,
                ExportedAt = DateTime.UtcNow.ToString("o"),
                Profile = entry.Profile
            };
            return JsonConvert.SerializeObject(exported, Formatting.Indented);
        }

        private void OnSaveToLibraryClick(object sender, RoutedEventArgs e)
        {
            var selectedItems = ListItems.SelectedItems
                .Cast<ProfileBrowserEntry>()
                .Where(entry => entry.Source == ProfileEntrySource.ImportedFile)
                .ToList();

            if (selectedItems.Count == 0)
                return;

            // Check for duplicates
            var existingKeys = _plugin?.Settings?.AircraftFfbProfiles?.Keys.ToHashSet() ?? new HashSet<string>();
            var duplicates = selectedItems.Where(x => !string.IsNullOrEmpty(x.ProfileKey) && existingKeys.Contains(x.ProfileKey)).ToList();

            if (duplicates.Count > 0)
            {
                string message = duplicates.Count == 1
                    ? $"Profile '{duplicates[0].Name}' already exists in your library.\n\nOverwrite it?"
                    : $"{duplicates.Count} profiles already exist in your library.\n\nOverwrite them?";

                var result = ThemedMessageBox.Show(this, message, "Duplicate Profiles",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                {
                    // Remove duplicates from the list to save
                    selectedItems = selectedItems.Except(duplicates).ToList();
                    if (selectedItems.Count == 0)
                        return;
                }
            }

            foreach (var entry in selectedItems)
            {
                // Save to settings
                if (_plugin?.Settings?.AircraftFfbProfiles != null && !string.IsNullOrEmpty(entry.ProfileKey))
                {
                    // Remove existing entry from _allStoredProfiles if overwriting
                    var existing = _allStoredProfiles.FirstOrDefault(x => x.ProfileKey == entry.ProfileKey);
                    if (existing != null)
                        _allStoredProfiles.Remove(existing);

                    _plugin.Settings.AircraftFfbProfiles[entry.ProfileKey] = entry.Profile;
                }

                // Move from staged to stored
                _stagedImports.Remove(entry);
                entry.Source = ProfileEntrySource.StoredProfile;
                _allStoredProfiles.Add(entry);
            }

            // Refresh filter and view
            PopulateGameFilter();

            // If no more staged imports, switch to appropriate game filter
            if (_stagedImports.Count == 0)
            {
                var lastSaved = selectedItems.LastOrDefault();
                if (lastSaved != null && !string.IsNullOrEmpty(lastSaved.GameId))
                    ComboGameFilter.SelectedItem = lastSaved.GameId;
                else
                    ComboGameFilter.SelectedItem = AllGamesFilter;
            }
            else
            {
                ApplyGameFilter(); // Stay on Imports
            }
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
