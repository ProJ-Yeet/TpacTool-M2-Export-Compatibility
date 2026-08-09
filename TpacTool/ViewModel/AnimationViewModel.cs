using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.CommandWpf;
using GalaSoft.MvvmLight.Threading;
using Microsoft.Win32;
using CommonServiceLocator;
using Ookii.Dialogs.Wpf;
using TpacTool.IO;
using TpacTool.IO.Assimp;
using TpacTool.Lib;
using TpacTool.Properties;

namespace TpacTool
{
	public class AnimationViewModel : ViewModelBase
	{
		private List<Skeleton> _skeletons = new List<Skeleton>();

		private Skeleton _human_skeleton;

		private Skeleton _horse_skeleton;

		public List<Skeleton> Skeletons => _skeletons;

		public int SelectedSkeletonIndex { set; get; } = -1;

		private List<Metamesh> _unfilteredModels = new List<Metamesh>();

		private List<Metamesh> _models = new List<Metamesh>();

		private Metamesh _male_head;

		private Metamesh _female_head;

		public List<Metamesh> Models => _models;

		public int SelectedModelIndex { set; get; } = -1;

		public ICommand ChangeSkeletonCommand { private set; get; }

		public ICommand ChangeModelCommand { private set; get; }

		public ICommand ChangeFrameCommand { private set; get; }

		public ICommand ExportAnimationCommand { private set; get; }

		public ICommand ExportAllAnimationsCommand { private set; get; }

		public ICommand ExportTpacAnimationCommand { private set; get; }

		public ICommand ExportClipsFromListCommand { private set; get; }

		public ICommand ExportMorphCommand { private set; get; }

		public ICommand ExportSingleAssetCommand { private set; get; }

		private SaveFileDialog _saveFileDialog;

		private SaveFileDialog _exportSaveFileDialog;

		private SkeletalAnimation _animationAsset;

		private MorphAnimation _morphAsset;

		private SkeletonType _exportedSkeletonType = SkeletonType.Default;

		private ModelType _exportedModelType = ModelType.MaleHead;

		public SkeletalAnimation AnimationAsset
		{
			set
			{
				if (value != null && value.Skeleton != Guid.Empty)
					DefaultSkeleton = _skeletons.Find(skeleton => skeleton.Guid == value.Skeleton);
				else
					DefaultSkeleton = null;
				_animationAsset = value;
				RaisePropertyChanged(nameof(DefaultSkeleton));
				RaisePropertyChanged(nameof(AnimationSkeletonName));
				RaisePropertyChanged(nameof(AnimationBoneCount));
				RaisePropertyChanged(nameof(ShowExportButton));
				RaisePropertyChanged(nameof(CanExportAll));
				RaisePropertyChanged(nameof(CanExportTpac));
			}
			get => _animationAsset;
		}

		public MorphAnimation MorphAsset
		{
			set
			{
				_morphAsset = value;
				RaisePropertyChanged(nameof(ShowExportButton));
			}
			get => _morphAsset;
		}

		public Skeleton DefaultSkeleton { private set; get; }

		public string AnimationSkeletonName
		{
			get
			{
				if (DefaultSkeleton != null)
					return DefaultSkeleton.Name;
				return String.Empty;
			}
		}

		public string AnimationBoneCount
		{
			get
			{
				if (_animationAsset != null)
					return _animationAsset.BoneNum.ToString();
				return String.Empty;
			}
		}

		public float FrameRate
		{
			set
			{
				Settings.Default.ExportAnimationFrameRate = Math.Min(Math.Max(value, 1f), 60f);
			}

			get
			{
				return Settings.Default.ExportAnimationFrameRate;
			}
		}

		public bool IsExportDefaultSkeleton => _exportedSkeletonType == SkeletonType.Default;

		public bool IsExportHumanSkeleton => _exportedSkeletonType == SkeletonType.Human;

		public bool IsExportHorseSkeleton => _exportedSkeletonType == SkeletonType.Horse;

		public bool IsExportOtherSkeleton => _exportedSkeletonType == SkeletonType.Other;

		public bool IsExportMaleHeadModel => _exportedModelType == ModelType.MaleHead;

		public bool IsExportFemaleHeadModel => _exportedModelType == ModelType.FemaleHead;

		public bool IsExportOtherHeadModel => _exportedModelType == ModelType.Other;

		public bool UseLargerScale
		{
			set => Settings.Default.ExportModelLargerScale = value;
			get => Settings.Default.ExportModelLargerScale;
		}

		public bool UseNegYForwardAxis
		{
			set => Settings.Default.ExportModelNegYForward = value;
			get => Settings.Default.ExportModelNegYForward;
		}

		public bool UseAscii
		{
			set => Settings.Default.ExportModelFbxAscii = value;
			get => Settings.Default.ExportModelFbxAscii;
		}

		public SkeletonType ExportedSkeletonType
		{
			set
			{
				_exportedSkeletonType = value;
				RaisePropertyChanged(nameof(ExportedSkeletonType));
				RaisePropertyChanged(nameof(IsExportOtherSkeleton));
			}
			get => _exportedSkeletonType;
		}

		public ModelType ExportedModelType
		{
			set
			{
				_exportedModelType = value;
				if (value == ModelType.Other && _models.Count == 0 && _unfilteredModels.Count > 0)
				{
					_models.AddRange(_unfilteredModels
						.Where(model => model.Meshes.Any(mesh => mesh.VertexKeyCount > 0)));
					_unfilteredModels.Clear();
				}
				RaisePropertyChanged(nameof(Models));
				RaisePropertyChanged(nameof(ExportedModelType));
				RaisePropertyChanged(nameof(IsExportOtherHeadModel));
			}
			get => _exportedModelType;
		}

		public bool CanExport => AssimpModelExporter.IsAssimpAvailable();

		public bool CanExportAll => AssimpModelExporter.IsAssimpAvailable();

		public bool CanExportTpac => AssimpModelExporter.IsAssimpAvailable() && AnimationAsset != null;

		public AnimationViewModel()
		{
			if (IsInDesignMode)
			{
			}
			else
			{
				_saveFileDialog = new SaveFileDialog();
				_saveFileDialog.CreatePrompt = false;
				_saveFileDialog.OverwritePrompt = true;
				_saveFileDialog.AddExtension = true;
				if (AssimpModelExporter.IsAssimpAvailable())
				{
					_saveFileDialog.Filter = "Autodesk FBX (*.fbx)|*.fbx";
					_saveFileDialog.FilterIndex = 1;
				}
				else
				{
					//_saveFileDialog.Filter = "COLLADA (*.dae)|*.dae";
					//_saveFileDialog.FilterIndex = 1;
				}
				_saveFileDialog.Title = Resources.Model_Dialog_SelectExportFile;

				_folderBrowserDialog = new VistaFolderBrowserDialog();

				_exportSaveFileDialog = new SaveFileDialog();
				_exportSaveFileDialog.CreatePrompt = false;
				_exportSaveFileDialog.OverwritePrompt = true;
				_exportSaveFileDialog.AddExtension = true;
				_exportSaveFileDialog.Filter = "TPAC (*.tpac)|*.tpac";
				_exportSaveFileDialog.FilterIndex = 1;
				_exportSaveFileDialog.Title = Resources.SaveFileDialog_SelectSaveFile;

				ChangeSkeletonCommand = new RelayCommand<string>(arg =>
				{
					SkeletonType.TryParse(arg, true, out SkeletonType result);
					ExportedSkeletonType = result;
				});

				ChangeModelCommand = new RelayCommand<string>(arg =>
				{
					ModelType.TryParse(arg, true, out ModelType result);
					ExportedModelType = result;
				});

				ChangeFrameCommand = new RelayCommand<string>(arg =>
				{
					float frame = 24f;
					if (float.TryParse(arg, out var value))
					{
						frame = value;
					}

					FrameRate = frame;
					RaisePropertyChanged(nameof(FrameRate));
				});

				ExportAnimationCommand = new RelayCommand(ExportAnimation);

				ExportAllAnimationsCommand = new RelayCommand(ExportAllAnimations);

				ExportTpacAnimationCommand = new RelayCommand(ExportTpacAnimation);

				ExportClipsFromListCommand = new RelayCommand(ExportClipsFromList);

				ExportMorphCommand = new RelayCommand(ExportMorph);

				ExportSingleAssetCommand = new RelayCommand(ExportSingleAsset);

				MessengerInstance.Register<AssetItem>(this, AssetTreeViewModel.AssetSelectedEvent, asset =>
				{
					if (asset is SkeletalAnimation animation)
						AnimationAsset = animation;
					else if (asset is MorphAnimation morph)
						MorphAsset = morph;
				});

				MessengerInstance.Register<IEnumerable<Skeleton>>(this, ModelViewModel.UpdateSkeletonListEvent, skeletons =>
				{
					_skeletons.Clear();
					_skeletons.AddRange(skeletons);

					foreach (var skeleton in _skeletons)
					{
						if (skeleton.Name == "human_skeleton")
							_human_skeleton = skeleton;
						else if (skeleton.Name == "horse_skeleton")
							_horse_skeleton = skeleton;
					}
					RaisePropertyChanged(nameof(Skeletons));
				});

				MessengerInstance.Register<IEnumerable<Metamesh>>(this, ModelViewModel.UpdateModelListEvent, models =>
				{
					_models.Clear();
					_unfilteredModels.Clear();
					_unfilteredModels.AddRange(models);

					foreach (var model in _unfilteredModels)
					{
						if (model.Name == "head_male_a")
							_male_head = model;
						else if (model.Name == "head_female_a")
							_female_head = model;
					}
					RaisePropertyChanged(nameof(Models));
				});

				MessengerInstance.Register<object>(this, MainViewModel.CleanupEvent, unused =>
				{
					_skeletons.Clear();
					_human_skeleton = null;
					_horse_skeleton = null;
					AnimationAsset = null;
					MorphAsset = null;
					_models.Clear();
					_unfilteredModels.Clear();
					_male_head = null;
					_female_head = null;
					RaisePropertyChanged(nameof(Skeletons));
					RaisePropertyChanged(nameof(Models));
					RaisePropertyChanged(nameof(AnimationAsset));
				});
			}
		}

		public void ExportAnimation()
		{
			Export(true, false);
		}

		public void ExportMorph()
		{
			Export(false, true);
		}

		private void Export(bool exportAnimation, bool exportMorph)
		{
			if (!AssimpModelExporter.IsAssimpAvailable())
			{
				MessageBox.Show(Resources.Msgbox_AnimationAssimpRequired,
					Resources.Msgbox_Error, MessageBoxButton.OK, MessageBoxImage.Stop);
				return;
			}

			Skeleton skeleton = null;

			if (exportAnimation)
			{
				switch (_exportedSkeletonType)
				{
					case SkeletonType.Human:
						if (_human_skeleton == null)
						{
							MessageBox.Show(Resources.Msgbox_HumanSkeletonNotFound,
								Resources.Msgbox_Error, MessageBoxButton.OK, MessageBoxImage.Stop);
							return;
						}
						skeleton = _human_skeleton;
						break;
					case SkeletonType.Horse:
						if (_horse_skeleton == null)
						{
							MessageBox.Show(Resources.Msgbox_HorseSkeletonNotFound,
								Resources.Msgbox_Error, MessageBoxButton.OK, MessageBoxImage.Stop);
							return;
						}
						skeleton = _horse_skeleton;
						break;
					case SkeletonType.Other:
						if (SelectedSkeletonIndex < 0)
						{
							MessageBox.Show(Resources.Msgbox_SkeletonNotSelected,
								Resources.Msgbox_Error, MessageBoxButton.OK, MessageBoxImage.Stop);
							return;
						}
						skeleton = Skeletons[SelectedSkeletonIndex];
						break;
					case SkeletonType.Default:
						if (DefaultSkeleton == null)
						{
							MessageBox.Show(Resources.Msgbox_DefaultSkeletonNotFound,
								Resources.Msgbox_Error, MessageBoxButton.OK, MessageBoxImage.Stop);
							return;
						}
						skeleton = DefaultSkeleton;
						break;
					default:
						throw new ArgumentOutOfRangeException();
				}
			}

			Metamesh model = null;
			if (exportMorph)
			{
				switch (_exportedModelType)
				{
					case ModelType.MaleHead:
						if (_male_head == null)
						{
							MessageBox.Show(Resources.Msgbox_MaleHeadModelNotFound,
								Resources.Msgbox_Error, MessageBoxButton.OK, MessageBoxImage.Stop);
							return;
						}
						model = _male_head;
						break;
					case ModelType.FemaleHead:
						if (_female_head == null)
						{
							MessageBox.Show(Resources.Msgbox_FemaleHeadModelNotFound,
								Resources.Msgbox_Error, MessageBoxButton.OK, MessageBoxImage.Stop);
							return;
						}
						model = _female_head;
						break;
					case ModelType.Other:
						if (SelectedModelIndex < 0)
						{
							MessageBox.Show(Resources.Msgbox_ModelNotSelected,
								Resources.Msgbox_Error, MessageBoxButton.OK, MessageBoxImage.Stop);
							return;
						}
						model = Models[SelectedModelIndex];
						break;
					default:
						throw new ArgumentOutOfRangeException();
				}
			}

			BuildExportOptions(out var option, out var assimpOption);

			SkeletalAnimation animation = exportAnimation ? AnimationAsset : null;
			MorphAnimation morph = exportMorph ? MorphAsset : null;
			var assetName = morph != null ? morph.Name : animation?.Name ?? "";

			_saveFileDialog.FileName = assetName;
			if (_saveFileDialog.ShowDialog().GetValueOrDefault(false))
			{
				var path = _saveFileDialog.FileName;

				MessengerInstance.Send(string.Format("Export {0} ...", assetName), MainViewModel.StatusEvent);
				if (AssimpModelExporter.IsAssimpAvailable())
				{
					AssimpModelExporter.ExportToFile(path, model, skeleton, animation, morph, option, assimpOption, FrameRate);
				}
				MessengerInstance.Send(string.Format("{0} exported", assetName), MainViewModel.StatusEvent);
			}
			}

		private void BuildExportOptions(out ModelExporter.ModelExportOption option,
			out AssimpModelExporter.AssimpModelExportOption assimpOption, bool includeLargerSize = true)
		{
			option = 0;
			assimpOption = 0;
			if (includeLargerSize && UseLargerScale)
				option |= ModelExporter.ModelExportOption.LargerSize;
			if (UseNegYForwardAxis)
				option |= ModelExporter.ModelExportOption.NegYAxisForward;
			if (UseAscii)
				assimpOption |= AssimpModelExporter.AssimpModelExportOption.UseAscii;
		}

		public void ExportAllAnimations()
		{
			if (!AssimpModelExporter.IsAssimpAvailable())
			{
				MessageBox.Show(Resources.Msgbox_AnimationAssimpRequired,
					Resources.Msgbox_Error, MessageBoxButton.OK, MessageBoxImage.Stop);
				return;
			}

			var mainVm = ServiceLocator.Current.GetInstance<MainViewModel>();
			var assetManager = mainVm.AssetManager;
			var allSkeletons = BuildSkeletonLookup(assetManager);

			// every skeletal animation across ALL loaded packages that belongs to a named skeleton;
			// animations without a usable skeleton are collected so the user can see why they were skipped
			var animations = new List<SkeletalAnimation>();
			var noSkeletonNames = new List<string>();
			foreach (var animation in assetManager.LoadedPackages.SelectMany(p => p.Items).OfType<SkeletalAnimation>())
			{
				if (animation.Definition == null ||
					animation.Skeleton == Guid.Empty ||
					!allSkeletons.TryGetValue(animation.Skeleton, out var skel) ||
					string.IsNullOrWhiteSpace(skel.Name))
				{
					noSkeletonNames.Add(animation.Name);
					continue;
				}
				animations.Add(animation);
			}

			if (animations.Count == 0)
				return;

			var clipsByAnimation = BuildClipsLookup(assetManager);
			if (ShowExportFolderDialog(out var targetDir))
				return;

			// batch export always uses natural scale and 24 fps, independent of the model-export toggles
			BuildExportOptions(out var option, out var assimpOption, false);
			RunBatchExport(targetDir, animations, allSkeletons, clipsByAnimation, option, assimpOption, 24f,
				FormatSkippedNames(noSkeletonNames));
		}

		public void ExportTpacAnimation()
		{
			if (!AssimpModelExporter.IsAssimpAvailable())
			{
				MessageBox.Show(Resources.Msgbox_AnimationAssimpRequired,
					Resources.Msgbox_Error, MessageBoxButton.OK, MessageBoxImage.Stop);
				return;
			}

			if (AnimationAsset == null)
			{
				MessageBox.Show(Resources.Msgbox_AnimationExportAllSelectAnimFirst,
					Resources.Msgbox_Error, MessageBoxButton.OK, MessageBoxImage.Stop);
				return;
			}

			var mainVm = ServiceLocator.Current.GetInstance<MainViewModel>();
			var assetManager = mainVm.AssetManager;
			var allSkeletons = BuildSkeletonLookup(assetManager);

			allSkeletons.TryGetValue(AnimationAsset.Skeleton, out var selectedSkeleton);
			if (selectedSkeleton == null || selectedSkeleton.Definition == null || selectedSkeleton.Definition.Data == null)
			{
				MessageBox.Show(Resources.Msgbox_DefaultSkeletonNotFound,
					Resources.Msgbox_Error, MessageBoxButton.OK, MessageBoxImage.Stop);
				return;
			}

			// only the selected animation's own tpac, limited to the same skeleton
			var package = assetManager.LoadedPackages.FirstOrDefault(p => p.Items.Contains(AnimationAsset));
			if (package == null)
				return;

			var animations = package.Items.OfType<SkeletalAnimation>()
				.Where(animation => animation.Definition != null &&
					animation.Skeleton != Guid.Empty &&
					allSkeletons.TryGetValue(animation.Skeleton, out var s) && s == selectedSkeleton)
				.ToList();
			if (animations.Count == 0)
				return;

			var clipsByAnimation = BuildClipsLookup(assetManager);
			if (ShowExportFolderDialog(out var targetDir))
				return;

			BuildExportOptions(out var option, out var assimpOption, false);
			RunBatchExport(targetDir, animations, allSkeletons, clipsByAnimation, option, assimpOption, 24f, null);
		}

		// Exports the SkeletalAnimations referenced by the AnimationClips named in a user-supplied
		// text list (one clip name per line; grouped headers / comments are ignored). Every clip
		// name resolves to an AnimationClip whose Animation guid points at a SkeletalAnimation, and
		// several clips may share one SkeletalAnimation, so the result is deduplicated per animation.
		public void ExportClipsFromList()
		{
			if (!AssimpModelExporter.IsAssimpAvailable())
			{
				MessageBox.Show(Resources.Msgbox_AnimationAssimpRequired,
					Resources.Msgbox_Error, MessageBoxButton.OK, MessageBoxImage.Stop);
				return;
			}

			var mainVm = ServiceLocator.Current.GetInstance<MainViewModel>();
			var assetManager = mainVm.AssetManager;

			if (_clipListDialog == null)
			{
				_clipListDialog = new OpenFileDialog
				{
					Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*",
					Title = Resources.Animation_ExportClipsList_SelectFile,
					CheckFileExists = true,
					Multiselect = false
				};
			}
			if (_clipListDialog.ShowDialog() != true)
				return;

			var clipNames = ParseClipNameList(_clipListDialog.FileName);
			if (clipNames.Count == 0)
			{
				MessageBox.Show(Resources.Msgbox_AnimationExportClipsListEmpty,
					Resources.Msgbox_Error, MessageBoxButton.OK, MessageBoxImage.Stop);
				return;
			}

			// clip name -> AnimationClip(s) across every loaded package
			var clipsByName = new Dictionary<string, List<AnimationClip>>(StringComparer.OrdinalIgnoreCase);
			foreach (var clip in assetManager.LoadedPackages.SelectMany(p => p.Items).OfType<AnimationClip>())
			{
				if (string.IsNullOrEmpty(clip.Name))
					continue;
				if (!clipsByName.TryGetValue(clip.Name, out var list))
				{
					list = new List<AnimationClip>();
					clipsByName[clip.Name] = list;
				}
				list.Add(clip);
			}

			// animation guid -> SkeletalAnimation
			var animByGuid = new Dictionary<Guid, SkeletalAnimation>();
			foreach (var anim in assetManager.LoadedPackages.SelectMany(p => p.Items).OfType<SkeletalAnimation>())
			{
				if (anim.Definition == null)
					continue;
				if (!animByGuid.ContainsKey(anim.Guid))
					animByGuid[anim.Guid] = anim;
			}

			var notFoundNames = new List<string>();
			var animations = new List<SkeletalAnimation>();
			var seenAnim = new HashSet<Guid>();
			foreach (var name in clipNames)
			{
				if (!clipsByName.TryGetValue(name, out var clips))
				{
					notFoundNames.Add(name);
					continue;
				}
				foreach (var clip in clips)
				{
					if (clip.Animation == Guid.Empty)
						continue;
					if (animByGuid.TryGetValue(clip.Animation, out var anim) && seenAnim.Add(anim.Guid))
						animations.Add(anim);
				}
			}

			// keep only animations that have a usable skeleton (same rule as ExportAllAnimations)
			var allSkeletons = BuildSkeletonLookup(assetManager);
			var noSkeletonNames = new List<string>();
			var exportable = new List<SkeletalAnimation>();
			foreach (var anim in animations)
			{
				if (anim.Skeleton == Guid.Empty ||
					!allSkeletons.TryGetValue(anim.Skeleton, out var skel) ||
					string.IsNullOrWhiteSpace(skel.Name))
				{
					noSkeletonNames.Add(anim.Name);
					continue;
				}
				exportable.Add(anim);
			}

			if (exportable.Count == 0)
			{
				var msg = string.Format(Resources.Msgbox_AnimationExportClipsListNoAnim,
					clipNames.Count, clipNames.Count - notFoundNames.Count);
				if (notFoundNames.Count > 0)
					msg += Environment.NewLine +
						string.Format(Resources.Msgbox_AnimationExportClipsListNotFound,
							string.Join(", ", notFoundNames));
				MessageBox.Show(msg, Resources.Msgbox_Error, MessageBoxButton.OK, MessageBoxImage.Stop);
				return;
			}

			var clipsByAnimation = BuildClipsLookup(assetManager);
			if (ShowExportFolderDialog(out var targetDir))
				return;

			BuildExportOptions(out var option, out var assimpOption, false);
			var notes = new List<string>
			{
				string.Format(Resources.Msgbox_AnimationExportClipsListFound,
					clipNames.Count, clipNames.Count - notFoundNames.Count, exportable.Count),
				Resources.Msgbox_AnimationExportClipsListAnimations + ": " +
					FormatAnimationNames(exportable.Select(a => a.Name).ToList())
			};
			if (notFoundNames.Count > 0)
				notes.Add(string.Format(Resources.Msgbox_AnimationExportClipsListNotFound,
					string.Join(", ", notFoundNames)));
			if (noSkeletonNames.Count > 0)
				notes.Add(string.Format(Resources.Msgbox_AnimationExportAllSkipped,
					string.Join(", ", noSkeletonNames)));

			RunBatchExport(targetDir, exportable, allSkeletons, clipsByAnimation, option, assimpOption, 24f, notes);
		}

		// extracts clip names from a plain-text list. valid names are simple identifiers
		// (e.g. guard_up_1h, blocked_slashright_1h_reverse); the grouped headers, separators and
		// explanatory comment lines of the anim_ref_list format are filtered out.
		private static List<string> ParseClipNameList(string path)
		{
			var result = new List<string>();
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var raw in File.ReadAllLines(path))
			{
				var line = raw.Trim();
				if (line.Length == 0 || !IdentifierRegex.IsMatch(line))
					continue;
				if (seen.Add(line))
					result.Add(line);
			}
			return result;
		}

		private static readonly Regex IdentifierRegex = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*$",
			RegexOptions.Compiled);

		// returns true when the user cancelled the folder dialog
		private bool ShowExportFolderDialog(out string targetDir)
		{
			_folderBrowserDialog.Description = Resources.Animation_ExportAll_SelectFolder;
			_folderBrowserDialog.UseDescriptionForTitle = true;
			if (_folderBrowserDialog.ShowDialog() != true)
			{
				targetDir = null;
				return true;
			}
			targetDir = _folderBrowserDialog.SelectedPath;
			return false;
		}

		private static Dictionary<Guid, Skeleton> BuildSkeletonLookup(AssetManager assetManager)
		{
			// the skeleton of an animation may live in a different tpac than the animation itself,
			// so look it up among ALL loaded skeletons across every loaded package; some animations
			// reference the skeleton's definition segment guid instead of the skeleton asset guid
			var allSkeletons = new Dictionary<Guid, Skeleton>();
			foreach (var skeleton in assetManager.LoadedPackages.SelectMany(p => p.Items).OfType<Skeleton>())
			{
				allSkeletons[skeleton.Guid] = skeleton;
				if (skeleton.Definition != null && skeleton.Definition.OwnerGuid != Guid.Empty)
					allSkeletons[skeleton.Definition.OwnerGuid] = skeleton;
			}
			return allSkeletons;
		}

		private static Dictionary<Guid, List<AnimationClip>> BuildClipsLookup(AssetManager assetManager)
		{
			// map each skeletal animation to the AnimationClips that reference it, so the exported
			// fbx clips can be named after the clips shown in the AnimationClip page
			var clipsByAnimation = new Dictionary<Guid, List<AnimationClip>>();
			foreach (var clip in assetManager.LoadedPackages.SelectMany(p => p.Items).OfType<AnimationClip>())
			{
				if (clip.Animation == Guid.Empty)
					continue;
				if (!clipsByAnimation.TryGetValue(clip.Animation, out var list))
				{
					list = new List<AnimationClip>();
					clipsByAnimation[clip.Animation] = list;
				}
				list.Add(clip);
			}
			return clipsByAnimation;
		}

		private static List<string> FormatSkippedNames(IEnumerable<string> names)
		{
			var list = names.ToList();
			const int maxShown = 15;
			var result = list.Take(maxShown)
				.Select(name => string.Format(Resources.Msgbox_AnimationExportAllSkipped, name))
				.ToList();
			if (list.Count > maxShown)
				result.Add(string.Format(Resources.Msgbox_AnimationExportAllSkippedMore, list.Count - maxShown));
			return result;
		}

		private static string FormatAnimationNames(List<string> names)
		{
			const int maxShown = 30;
			if (names.Count <= maxShown)
				return string.Join(", ", names);
			return string.Join(", ", names.Take(maxShown)) +
				string.Format(Resources.Msgbox_AnimationExportAllSkippedMore, names.Count - maxShown);
		}

		private void RunBatchExport(string targetDir,
			IReadOnlyList<SkeletalAnimation> animations,
			Dictionary<Guid, Skeleton> allSkeletons,
			Dictionary<Guid, List<AnimationClip>> clipsByAnimation,
			ModelExporter.ModelExportOption option, AssimpModelExporter.AssimpModelExportOption assimpOption,
			float frameRate, List<string> extraNotes)
		{
			MessengerInstance.Send("Export all animations ...", MainViewModel.StatusEvent);
			Task.Run(() =>
			{
				int exportedCount = 0;
				int skippedCount = 0;
				var skippedDetails = new List<string>();

				// one output subfolder per skeleton (created on demand)
				var bySkeleton = animations
					.GroupBy(animation => allSkeletons[animation.Skeleton])
					.ToList();

				foreach (var group in bySkeleton)
				{
					var skeleton = group.Key;
					var skelDirName = SanitizeFileName(skeleton.Name);
					if (string.IsNullOrWhiteSpace(skelDirName))
						skelDirName = skeleton.Guid.ToString();
					var skelDir = Path.Combine(targetDir, skelDirName);
					Directory.CreateDirectory(skelDir);
					var usedFileNames = new HashSet<string>();

					foreach (var animation in group)
					{
						// one fbx per skeletal animation; the clips inside are the AnimationClips
						// that reference it, named after those clips (falling back to the animation
						// name when no clip references it)
						var clips = clipsByAnimation.TryGetValue(animation.Guid, out var list) ? list : null;
						var clipNames = clips != null && clips.Count > 0
							? clips.Select(clip => clip.Name).ToList()
							: new List<string> { animation.Name };
						// each clip carries its own start/end frame inside the shared skeletal animation
						// (AnimationClip.Source1..Source2); -1 end means "whole animation"
						var frameRanges = clips != null && clips.Count > 0
							? clips.Select(clip => Tuple.Create(clip.Source1, clip.Source2)).ToList()
							: new List<Tuple<float, float>> { Tuple.Create(0f, -1f) };

						// the fbx is named after the skeletal animation (not the clip), so it is easy
						// to find; the clips inside still use the referencing AnimationClip names
						var fileName = SanitizeFileName(animation.Name);
						if (string.IsNullOrWhiteSpace(fileName))
							fileName = animation.Guid.ToString();
						fileName = GetUniqueFileName(fileName, usedFileNames) + ".fbx";

						try
						{
							// repeat the animation once per clip so every clip becomes its own named
							// animation inside the fbx
							var animList = new List<SkeletalAnimation>(clipNames.Count);
							for (int i = 0; i < clipNames.Count; i++)
								animList.Add(animation);

							AssimpModelExporter.ExportToFile(Path.Combine(skelDir, fileName), null,
								skeleton, animList, option, assimpOption, frameRate, clipNames, frameRanges);
							exportedCount++;
						}
						catch (Exception e)
						{
							skippedCount++;
							skippedDetails.Add(string.Format(Resources.Msgbox_AnimationExportAllFailed,
								animation.Name, e.GetType().Name + ": " + e.Message));
						}
					}
				}

				var summary = string.Format(Resources.Msgbox_AnimationExportAllDone, exportedCount, skippedCount);
				if (extraNotes != null && extraNotes.Count > 0)
					summary += Environment.NewLine + string.Join(Environment.NewLine, extraNotes);
				if (skippedDetails.Count > 0)
					summary += Environment.NewLine + string.Join(Environment.NewLine, skippedDetails);

				DispatcherHelper.CheckBeginInvokeOnUI(() =>
				{
					MessengerInstance.Send(string.Format("{0} animation file(s) exported", exportedCount),
						MainViewModel.StatusEvent);
					MessageBox.Show(summary, Resources.Msgbox_Info,
						MessageBoxButton.OK, MessageBoxImage.Information);
				});
			});
		}

		private static string GetUniqueFileName(string baseName, HashSet<string> used)
		{
			var candidate = baseName;
			int suffix = 2;
			while (used.Contains(candidate))
				candidate = string.Format("{0}_{1}", baseName, suffix++);
			used.Add(candidate);
			return candidate;
		}

		private static string SanitizeFileName(string name)
		{
			var invalid = Path.GetInvalidFileNameChars();
			return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
		}

			private VistaFolderBrowserDialog _folderBrowserDialog;

			private OpenFileDialog _clipListDialog;

		public bool ShowExportButton
		{
			get
			{
				var asset = AnimationAsset as AssetItem ?? MorphAsset as AssetItem;
				if (asset == null)
					return false;
				var mainVm = ServiceLocator.Current.GetInstance<MainViewModel>();
				var source = mainVm.AssetManager.LoadedPackages.Where(p => p.Items.Contains(asset));
				return source.Any(p => p.Items.Count > 1);
			}
		}

		private void ExportSingleAsset()
		{
			var asset = AnimationAsset as AssetItem ?? MorphAsset as AssetItem;
			if (asset == null)
				return;

			var mainVm = ServiceLocator.Current.GetInstance<MainViewModel>();
			var source = mainVm.AssetManager.LoadedPackages.Where(p => p.Items.Contains(asset));
			if (source.Any())
			{
				var assetPackage = source.First();
				var suffix = AssetPackage.GetTypeSuffix(asset.Type);
				_exportSaveFileDialog.FileName = asset.Name + suffix;
				if (_exportSaveFileDialog.ShowDialog() == true)
				{
					var filePath = _exportSaveFileDialog.FileName;
					assetPackage.ExportSingleAsset(asset, System.IO.Path.GetDirectoryName(filePath), System.IO.Path.GetFileNameWithoutExtension(filePath));
					MessengerInstance.Send<object>(null, AssetTreeViewModel.RefreshEvent);
				}
			}
		}
	}
}