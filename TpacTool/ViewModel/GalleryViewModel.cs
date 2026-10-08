using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.CommandWpf;
using GalaSoft.MvvmLight.Messaging;
using Ookii.Dialogs.Wpf;
using TpacTool.IO.Assimp;
using TpacTool.Lib;
using TpacTool.Properties;
using Material = TpacTool.Lib.Material;

namespace TpacTool
{
	public sealed class GalleryRow
	{
		public IReadOnlyList<GalleryItem> Items { get; }

		public GalleryRow(IReadOnlyList<GalleryItem> items)
		{
			Items = items;
		}
	}

	public class GalleryViewModel : ViewModelBase
	{
		private const string SkeletonAuto = "Auto (human; horse for horse/camel gear)";
		private const string SkeletonNone = "None (export as static mesh)";

		private readonly List<GalleryItem> _allItems = new List<GalleryItem>();
		private readonly List<Skeleton> _skeletons;
		private readonly Skeleton _humanSkeleton;
		private readonly Skeleton _horseSkeleton;
		private List<GalleryItem> _visibleItems = new List<GalleryItem>();
		private IReadOnlyList<GalleryRow> _rows = new GalleryRow[0];
		private int _kindIndex;
		private string _filterText = string.Empty;
		private bool _showSelectedOnly;
		private int _columns = 4;
		private GalleryItem _anchor;
		private bool _suspendSelectionUpdate;
		private CancellationTokenSource _exportCancel;
		private bool _isExporting;
		private double _exportProgress;
		private string _exportStatus = string.Empty;
		private string _lastExportDir;

		public string[] KindItems { get; } = { "Models", "Textures", "Materials" };

		public string[] FormatItems { get; }

		public string[] TextureFormatItems { get; } = { "Best for each texture", "PNG when possible", "DDS when possible" };

		public string[] SkeletonItems { get; }

		public string HumanSkeletonInfo { get; }

		public IReadOnlyList<GalleryRow> Rows
		{
			get => _rows;
			private set => Set(ref _rows, value);
		}

		public int KindIndex
		{
			get => _kindIndex;
			set
			{
				if (Set(ref _kindIndex, value))
				{
					_anchor = null;
					Refilter();
				}
			}
		}

		public string FilterText
		{
			get => _filterText;
			set
			{
				if (Set(ref _filterText, value ?? string.Empty))
					Refilter();
			}
		}

		public bool ShowSelectedOnly
		{
			get => _showSelectedOnly;
			set
			{
				if (Set(ref _showSelectedOnly, value))
					Refilter();
			}
		}

		public int ThumbnailSize
		{
			get => Math.Max(64, Math.Min(256, Settings.Default.GalleryThumbnailSize));
			set
			{
				Settings.Default.GalleryThumbnailSize = value;
				RaisePropertyChanged(nameof(ThumbnailSize));
				RaisePropertyChanged(nameof(TileWidth));
			}
		}

		/// <summary>Width of one tile including its margin; the window uses it to work out the column count.</summary>
		public double TileWidth => ThumbnailSize + 14;

		public int Columns
		{
			get => _columns;
			set
			{
				value = Math.Max(1, value);
				if (Set(ref _columns, value))
					RebuildRows();
			}
		}

		public string VisibleSummary => $"{_visibleItems.Count} shown";

		public int SelectedCount => _allItems.Count(i => i.IsSelected);

		public string SelectionSummary
		{
			get
			{
				var models = _allItems.Count(i => i.IsSelected && i.Kind == GalleryAssetKind.Model);
				var textures = _allItems.Count(i => i.IsSelected && i.Kind == GalleryAssetKind.Texture);
				var materials = _allItems.Count(i => i.IsSelected && i.Kind == GalleryAssetKind.Material);
				if (models + textures + materials == 0)
					return "Nothing selected";
				var parts = new List<string>();
				if (models > 0) parts.Add($"{models} model{(models == 1 ? "" : "s")}");
				if (materials > 0) parts.Add($"{materials} material{(materials == 1 ? "" : "s")}");
				if (textures > 0) parts.Add($"{textures} texture{(textures == 1 ? "" : "s")}");
				return string.Join(", ", parts) + " selected";
			}
		}

		public string ExportButtonText => SelectedCount > 0 ? $"Export {SelectedCount} selected" : "Export selected";

		#region Export settings

		public string OutputDir
		{
			get => Settings.Default.GalleryExportDir;
			set
			{
				Settings.Default.GalleryExportDir = value ?? string.Empty;
				RaisePropertyChanged(nameof(OutputDir));
			}
		}

		public int FormatIndex { get; set; }

		public int TextureFormatIndex { get; set; }

		public int SkeletonIndex { get; set; }

		public bool OneFolderPerAsset { get; set; } = true;

		public bool ConvertToTPose
		{
			get => Settings.Default.ExportModelTPose;
			set => Settings.Default.ExportModelTPose = value;
		}

		/// <summary>What the T-pose checkbox shows: always on for medieval 2 (its skeletons are in T-pose).</summary>
		public bool TPoseChecked
		{
			get => ConvertToTPose || IsM2Target;
			set
			{
				if (!IsM2Target)
					ConvertToTPose = value;
				RaisePropertyChanged(nameof(TPoseChecked));
			}
		}

		public bool StraightenElbows
		{
			get => Settings.Default.ExportModelTPoseStraightenElbows;
			set => Settings.Default.ExportModelTPoseStraightenElbows = value;
		}

		public bool AllLods
		{
			get => Settings.Default.ExportModelAllLods;
			set => Settings.Default.ExportModelAllLods = value;
		}

		public bool DiffuseOnly
		{
			get => Settings.Default.ExportModelDiffuseOnly;
			set => Settings.Default.ExportModelDiffuseOnly = value;
		}

		public bool LargerScale
		{
			get => Settings.Default.ExportModelLargerScale;
			set => Settings.Default.ExportModelLargerScale = value;
		}

		public bool NegYForward
		{
			get => Settings.Default.ExportModelNegYForward;
			set => Settings.Default.ExportModelNegYForward = value;
		}

		public bool YUp
		{
			get => Settings.Default.ExportModelObjYUp;
			set => Settings.Default.ExportModelObjYUp = value;
		}

		public string[] RigTargetItems { get; } = { "Bannerlord skeleton", "Medieval 2 skeleton (transfer rig)" };

		public int RigTargetIndex
		{
			get => Settings.Default.GalleryRigTarget == 1 ? 1 : 0;
			set
			{
				Settings.Default.GalleryRigTarget = value;
				RaisePropertyChanged(nameof(RigTargetIndex));
				RaisePropertyChanged(nameof(IsM2Target));
				RaisePropertyChanged(nameof(IsBannerlordTarget));
				RaisePropertyChanged(nameof(TPoseChecked));
			}
		}

		public bool IsM2Target => RigTargetIndex == 1;

		public bool IsBannerlordTarget => !IsM2Target;

		/// <summary>
		/// Optional: a Medieval 2 Toolkit armatures folder (Sword.glb, Spear.glb, ...) to use instead of the
		/// skeletons bundled with TpacTool.
		/// </summary>
		public string M2ArmaturesDir
		{
			get => Settings.Default.M2CustomArmaturesDir;
			set
			{
				Settings.Default.M2CustomArmaturesDir = value ?? string.Empty;
				RaisePropertyChanged(nameof(M2ArmaturesDir));
				RefreshM2Skeletons();
			}
		}

		private bool UsesCustomM2Dir => !string.IsNullOrWhiteSpace(M2ArmaturesDir) &&
		                                M2RigTransfer.FindSkeletonFiles(M2ArmaturesDir).Count > 0;

		private string EffectiveM2Dir => UsesCustomM2Dir ? M2ArmaturesDir : M2RigTransfer.BundledSkeletonsDir;

		public bool M2IncludeAnimations
		{
			get => Settings.Default.M2IncludeAnimations;
			set => Settings.Default.M2IncludeAnimations = value;
		}

		public string[] M2SkeletonItems { get; private set; } = new string[0];

		public int M2SkeletonIndex
		{
			get => Array.FindIndex(M2SkeletonItems, n => n.Equals(Settings.Default.M2Skeleton, StringComparison.OrdinalIgnoreCase));
			set
			{
				if (value >= 0 && value < M2SkeletonItems.Length)
					Settings.Default.M2Skeleton = M2SkeletonItems[value];
				RaisePropertyChanged(nameof(M2SkeletonIndex));
			}
		}

		public string M2Info => M2SkeletonItems.Length == 0
			? "No Medieval 2 skeletons found (the M2Skeletons folder next to TpacTool.exe is missing)."
			: (UsesCustomM2Dir ? "Using the skeletons in the custom folder. " : "Using the bundled skeletons (leave the folder empty). ") +
			  "Rigged human models are T-posed and their weights moved onto the chosen Medieval 2 skeleton " +
			  "(twist bones, toes, fingers and the neck are merged). Axis options are ignored.";

		private void RefreshM2Skeletons()
		{
			M2SkeletonItems = M2RigTransfer.FindSkeletonFiles(EffectiveM2Dir)
				.Select(Path.GetFileNameWithoutExtension).ToArray();
			if (M2SkeletonItems.Length > 0 && M2SkeletonIndex < 0)
				Settings.Default.M2Skeleton = M2SkeletonItems.FirstOrDefault(n => n.Equals("Sword", StringComparison.OrdinalIgnoreCase)) ??
				                              M2SkeletonItems[0];
			RaisePropertyChanged(nameof(M2SkeletonItems));
			RaisePropertyChanged(nameof(M2SkeletonIndex));
			RaisePropertyChanged(nameof(M2Info));
		}


		#endregion

		public bool IsExporting
		{
			get => _isExporting;
			private set
			{
				if (Set(ref _isExporting, value))
					RaisePropertyChanged(nameof(IsIdle));
			}
		}

		public bool IsIdle => !_isExporting;

		public double ExportProgress
		{
			get => _exportProgress;
			private set => Set(ref _exportProgress, value);
		}

		public string ExportStatus
		{
			get => _exportStatus;
			private set => Set(ref _exportStatus, value);
		}

		public bool CanOpenOutput => !string.IsNullOrEmpty(_lastExportDir);

		public ICommand SelectAllCommand { get; }
		public ICommand SelectNoneCommand { get; }
		public ICommand InvertSelectionCommand { get; }
		public ICommand ClearFilterCommand { get; }
		public ICommand BrowseOutputCommand { get; }
		public ICommand BrowseM2Command { get; }
		public ICommand ExportCommand { get; }
		public ICommand CancelExportCommand { get; }
		public ICommand OpenOutputCommand { get; }

		public GalleryViewModel(AssetManager assetManager)
		{
			FormatItems = AssimpModelExporter.IsAssimpAvailable()
				? new[] { "Autodesk FBX (*.fbx)", "Wavefront OBJ (*.obj)", "COLLADA (*.dae)" }
				: new[] { "Wavefront OBJ (*.obj)", "COLLADA (*.dae)" };

			// mods often ship the human skeleton under another name (e.g. "human_skeleton_notused.004"), so
			// nothing is filtered out here. when the folder has no human skeleton at all, the built-in copy
			// of bannerlord's human skeleton is used
			_skeletons = assetManager.LoadedAssets.OfType<Skeleton>()
				.Where(s => s.Definition?.Data != null)
				.OrderBy(s => s.Name).ToList();
			var loadedHuman = BuiltInSkeleton.FindLoadedHuman(_skeletons);
			_humanSkeleton = loadedHuman ?? BuiltInSkeleton.Human;
			_horseSkeleton = BuiltInSkeleton.FindLoadedHorse(_skeletons);
			HumanSkeletonInfo = loadedHuman != null
				? "Human skeleton: " + loadedHuman.Name
				: "Human skeleton: built-in copy (none in the loaded folder)";
			if (loadedHuman == null)
				_skeletons.Insert(0, BuiltInSkeleton.Human);
			SkeletonItems = new[] { SkeletonAuto, SkeletonNone }
				.Concat(_skeletons.Select(s => "Always: " + (s == BuiltInSkeleton.Human ? BuiltInSkeleton.HumanName : s.Name)))
				.ToArray();

			foreach (var asset in assetManager.LoadedAssets.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
			{
				if (asset is Metamesh || asset is Texture || asset is Material)
					_allItems.Add(new GalleryItem(asset, OnItemSelectionChanged));
			}

			SelectAllCommand = new RelayCommand(() => SetSelection(_visibleItems, true));
			SelectNoneCommand = new RelayCommand(() => SetSelection(_allItems, false));
			InvertSelectionCommand = new RelayCommand(() =>
			{
				_suspendSelectionUpdate = true;
				foreach (var item in _visibleItems)
					item.IsSelected = !item.IsSelected;
				_suspendSelectionUpdate = false;
				OnSelectionChanged();
			});
			ClearFilterCommand = new RelayCommand(() => FilterText = string.Empty);
			BrowseOutputCommand = new RelayCommand(BrowseOutput);
			BrowseM2Command = new RelayCommand(() =>
			{
				var dialog = new VistaFolderBrowserDialog
				{
					Description = "Select the Medieval 2 Toolkit's armatures folder (with Sword.glb, Spear.glb, ...)",
					UseDescriptionForTitle = true
				};
				if (Directory.Exists(M2ArmaturesDir))
					dialog.SelectedPath = M2ArmaturesDir + Path.DirectorySeparatorChar;
				if (dialog.ShowDialog().GetValueOrDefault(false))
					M2ArmaturesDir = dialog.SelectedPath;
			});
			RefreshM2Skeletons();
			ExportCommand = new RelayCommand(Export);
			CancelExportCommand = new RelayCommand(() => _exportCancel?.Cancel());
			OpenOutputCommand = new RelayCommand(() =>
			{
				if (Directory.Exists(_lastExportDir))
					Process.Start("explorer.exe", "\"" + _lastExportDir + "\"");
			});

			Refilter();
		}

		#region Filtering & layout

		private void Refilter()
		{
			var kind = (GalleryAssetKind) KindIndex;
			var terms = _filterText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
			_visibleItems = _allItems.Where(i => i.Kind == kind &&
			                                     (!_showSelectedOnly || i.IsSelected) &&
			                                     (terms.Length == 0 || i.Matches(terms))).ToList();
			RaisePropertyChanged(nameof(VisibleSummary));
			RebuildRows();
		}

		private void RebuildRows()
		{
			var rows = new List<GalleryRow>((_visibleItems.Count + _columns - 1) / _columns);
			for (int i = 0; i < _visibleItems.Count; i += _columns)
				rows.Add(new GalleryRow(_visibleItems.GetRange(i, Math.Min(_columns, _visibleItems.Count - i))));
			Rows = rows;
		}

		#endregion

		#region Selection

		/// <summary>Click on a tile: toggles it; shift-click selects the range from the last clicked tile.</summary>
		public void OnTileClicked(GalleryItem item, bool shift)
		{
			if (shift && _anchor != null)
			{
				var a = _visibleItems.IndexOf(_anchor);
				var b = _visibleItems.IndexOf(item);
				if (a >= 0 && b >= 0)
				{
					var target = _anchor.IsSelected;
					SetSelection(_visibleItems.GetRange(Math.Min(a, b), Math.Abs(a - b) + 1), target);
					return;
				}
			}
			item.IsSelected = !item.IsSelected;
			_anchor = item;
		}

		/// <summary>Double click: show the asset in the main window.</summary>
		public void OpenInMainWindow(GalleryItem item)
		{
			Messenger.Default.Send<AssetItem>(item.Asset, AssetTreeViewModel.AssetSelectedEvent);
			var main = Application.Current.MainWindow;
			if (main != null)
			{
				if (main.WindowState == WindowState.Minimized)
					main.WindowState = WindowState.Normal;
				main.Activate();
			}
		}

		private void SetSelection(IEnumerable<GalleryItem> items, bool selected)
		{
			_suspendSelectionUpdate = true;
			foreach (var item in items)
				item.IsSelected = selected;
			_suspendSelectionUpdate = false;
			OnSelectionChanged();
		}

		private void OnItemSelectionChanged(GalleryItem item)
		{
			if (!_suspendSelectionUpdate)
				OnSelectionChanged();
		}

		private void OnSelectionChanged()
		{
			RaisePropertyChanged(nameof(SelectedCount));
			RaisePropertyChanged(nameof(SelectionSummary));
			RaisePropertyChanged(nameof(ExportButtonText));
			if (_showSelectedOnly)
				Refilter();
		}

		#endregion

		#region Export

		private void BrowseOutput()
		{
			var dialog = new VistaFolderBrowserDialog
			{
				Description = "Select the folder the selected assets are exported to",
				UseDescriptionForTitle = true,
				ShowNewFolderButton = true
			};
			if (Directory.Exists(OutputDir))
				dialog.SelectedPath = OutputDir + Path.DirectorySeparatorChar;
			if (dialog.ShowDialog().GetValueOrDefault(false))
				OutputDir = dialog.SelectedPath;
		}

		private GalleryExportSettings BuildSettings()
		{
			var format = FormatItems[Math.Max(0, Math.Min(FormatIndex, FormatItems.Length - 1))];
			var settings = new GalleryExportSettings
			{
				OutputDir = OutputDir,
				ModelExtension = format.Contains("FBX") ? "fbx" : format.Contains("OBJ") ? "obj" : "dae",
				TexturePreference = TextureFormatIndex,
				OneFolderPerAsset = OneFolderPerAsset,
				HumanSkeleton = _humanSkeleton,
				HorseSkeleton = _horseSkeleton,
				ConvertToTPose = ConvertToTPose,
				StraightenElbows = StraightenElbows,
				AllLods = AllLods,
				DiffuseOnly = DiffuseOnly,
				LargerScale = LargerScale,
				NegYForward = NegYForward,
				YUp = YUp
			};
			if (IsM2Target)
			{
				var index = M2SkeletonIndex;
				if (index < 0)
					throw new InvalidOperationException("No Medieval 2 skeleton selected: pick the toolkit's armatures folder first.");
				settings.M2SkeletonPath = M2RigTransfer.FindSkeletonFiles(EffectiveM2Dir)[index];
				settings.M2IncludeAnimations = M2IncludeAnimations;
				settings.ConvertToTPose = true;
			}
			if (SkeletonIndex == 1)
				settings.SkeletonMode = GallerySkeletonMode.None;
			else if (SkeletonIndex >= 2 && SkeletonIndex - 2 < _skeletons.Count)
			{
				settings.SkeletonMode = GallerySkeletonMode.Specific;
				settings.Skeleton = _skeletons[SkeletonIndex - 2];
			}
			return settings;
		}

		private void Export()
		{
			if (IsExporting)
				return;
			var selected = _allItems.Where(i => i.IsSelected).ToList();
			if (selected.Count == 0)
			{
				MessageBox.Show("Select one or more assets first: click tiles to select them " +
				                "(shift-click selects a range).", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
				return;
			}

			if (string.IsNullOrWhiteSpace(OutputDir))
				BrowseOutput();
			if (string.IsNullOrWhiteSpace(OutputDir))
				return;
			try
			{
				Directory.CreateDirectory(OutputDir);
			}
			catch (Exception e)
			{
				MessageBox.Show("Cannot create the output folder:\n" + e.Message, Resources.Msgbox_Error,
					MessageBoxButton.OK, MessageBoxImage.Error);
				return;
			}
			Settings.Default.Save();

			GalleryExportSettings settings;
			try
			{
				settings = BuildSettings();
			}
			catch (InvalidOperationException e)
			{
				MessageBox.Show(e.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Information);
				return;
			}
			var exporter = new GalleryExporter(settings);
			var cancel = new CancellationTokenSource();
			_exportCancel = cancel;
			IsExporting = true;
			ExportProgress = 0;
			_lastExportDir = settings.OutputDir;
			RaisePropertyChanged(nameof(CanOpenOutput));
			var dispatcher = Application.Current.Dispatcher;

			Task.Run(() =>
			{
				var failures = new List<string>();
				var warnings = new List<string>();
				var log = new List<string>();
				int done = 0;
				foreach (var item in selected)
				{
					if (cancel.IsCancellationRequested)
						break;
					var index = done;
					dispatcher.BeginInvoke(new Action(() =>
					{
						// same priority as the completion message below, so a late progress update
						// can never overwrite it
						if (!IsExporting)
							return;
						ExportStatus = $"Exporting {index + 1}/{selected.Count}: {item.Name}";
						ExportProgress = 100.0 * index / selected.Count;
					}));
					try
					{
						var note = exporter.Export(item.Asset);
						log.Add(item.Name + (note != null ? " - " + note : ""));
						if (note != null && (note.Contains("exported static") || note.Contains("skipped")))
							warnings.Add($"{item.Name}: {note}");
					}
					catch (Exception e)
					{
						failures.Add($"{item.Name}: {e.Message}");
						log.Add($"{item.Name} - FAILED: {e}");
					}
					done++;
				}

				try
				{
					File.WriteAllLines(Path.Combine(settings.OutputDir, "export_log.txt"),
						new[] { $"TpacTool gallery export {DateTime.Now:yyyy-MM-dd HH:mm:ss}", "" }.Concat(log));
				}
				catch (Exception)
				{
					// the log is a convenience only
				}

				dispatcher.BeginInvoke(new Action(() =>
				{
					IsExporting = false;
					ExportProgress = 100.0 * done / selected.Count;
					var ok = done - failures.Count;
					var cancelled = cancel.IsCancellationRequested && done < selected.Count;
					ExportStatus = $"{(cancelled ? "Cancelled: " : "Done: ")}{ok} exported" +
					               (failures.Count > 0 ? $", {failures.Count} failed" : "") +
					               (warnings.Count > 0 ? $", {warnings.Count} with warnings" : "") +
					               $" → {settings.OutputDir}";
					Messenger.Default.Send(ExportStatus, MainViewModel.StatusEvent);
					if (failures.Count > 0)
					{
						MessageBox.Show($"{failures.Count} of {done} assets could not be exported:\n\n" +
						                string.Join("\n", failures.Take(15)) +
						                (failures.Count > 15 ? $"\n… and {failures.Count - 15} more" : "") +
						                "\n\nSee export_log.txt in the output folder for details.",
							"Export", MessageBoxButton.OK, MessageBoxImage.Warning);
					}
					else if (warnings.Count > 0)
					{
						MessageBox.Show($"{warnings.Count} assets were exported with warnings:\n\n" +
						                string.Join("\n", warnings.Take(15)) +
						                (warnings.Count > 15 ? $"\n… and {warnings.Count - 15} more" : "") +
						                "\n\nSee export_log.txt in the output folder for details.",
							"Export", MessageBoxButton.OK, MessageBoxImage.Warning);
					}
				}));
			});
		}

		#endregion

		public override void Cleanup()
		{
			_exportCancel?.Cancel();
			ThumbnailService.Clear();
			base.Cleanup();
		}
	}
}
