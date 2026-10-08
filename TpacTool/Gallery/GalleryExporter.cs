using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TpacTool.IO;
using TpacTool.IO.Assimp;
using TpacTool.Lib;
using Material = TpacTool.Lib.Material;

namespace TpacTool
{
	public enum GallerySkeletonMode
	{
		/// <summary>Rigged models use the human skeleton (horse skeleton for horse/camel gear).</summary>
		Auto,
		/// <summary>Export every model as a static mesh.</summary>
		None,
		/// <summary>Rigged models use <see cref="GalleryExportSettings.Skeleton"/>.</summary>
		Specific
	}

	public sealed class GalleryExportSettings
	{
		public string OutputDir;
		public string ModelExtension = "fbx"; // fbx, obj or dae
		public int TexturePreference; // 0 = best for the format, 1 = png, 2 = dds
		public bool OneFolderPerAsset = true;
		public GallerySkeletonMode SkeletonMode = GallerySkeletonMode.Auto;
		public Skeleton Skeleton;
		public Skeleton HumanSkeleton;
		public Skeleton HorseSkeleton;
		public bool ConvertToTPose;
		public bool StraightenElbows = true;
		public bool AllLods;
		public bool DiffuseOnly;
		public bool LargerScale;
		public bool NegYForward;
		public bool YUp;
		/// <summary>When set, rigged human models are transferred onto this Medieval 2 skeleton (.glb).</summary>
		public string M2SkeletonPath;
		/// <summary>Write the m2 skeleton's animations into the fbx too.</summary>
		public bool M2IncludeAnimations = true;
	}

	/// <summary>
	/// Exports gallery selections: models with all their textures, materials as their texture sets and
	/// plain textures. Safe to run on a background thread.
	/// </summary>
	public sealed class GalleryExporter
	{
		private static readonly string[] HorseKeywords = { "horse", "camel", "mule", "donkey", "pony" };

		private readonly GalleryExportSettings _settings;
		private readonly HashSet<string> _usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		private Skeleton _m2Skeleton;
		private List<global::Assimp.Animation> _m2Animations;

		public GalleryExporter(GalleryExportSettings settings)
		{
			_settings = settings;
		}

		/// <summary>Exports one asset and returns a short note for the log (may be null).</summary>
		public string Export(AssetItem asset)
		{
			switch (asset)
			{
				case Metamesh metamesh:
					return ExportModel(metamesh);
				case Material material:
					return ExportMaterial(material);
				case Texture texture:
					return ExportTexture(texture);
			}
			throw new NotSupportedException("Cannot export " + asset.GetType().Name);
		}

		private string ExportModel(Metamesh model)
		{
			var name = UniqueName(model.Name);
			var dir = _settings.OneFolderPerAsset ? Path.Combine(_settings.OutputDir, name) : _settings.OutputDir;
			Directory.CreateDirectory(dir);
			var path = Path.Combine(dir, name + "." + _settings.ModelExtension);

			var note = new List<string>();
			var skeleton = PickSkeleton(model, note);

			ModelExporter.ModelExportOption option = 0;
			// one folder per asset: textures sit next to the model. otherwise: <model name>/ sub folder
			option |= _settings.OneFolderPerAsset
				? ModelExporter.ModelExportOption.ExportTextures
				: ModelExporter.ModelExportOption.ExportTexturesSubFolder;
			if (_settings.TexturePreference == 1)
				option |= ModelExporter.ModelExportOption.PreferPng;
			else if (_settings.TexturePreference == 2)
				option |= ModelExporter.ModelExportOption.PreferDds;
			if (_settings.AllLods)
				option |= ModelExporter.ModelExportOption.ExportAllLod;
			if (_settings.DiffuseOnly)
				option |= ModelExporter.ModelExportOption.ExportDiffuseOnly;
			if (_settings.LargerScale)
				option |= ModelExporter.ModelExportOption.LargerSize;
			if (_settings.NegYForward)
				option |= ModelExporter.ModelExportOption.NegYAxisForward;
			if (_settings.YUp)
				option |= ModelExporter.ModelExportOption.YAxisUp;
			if (_settings.ConvertToTPose && skeleton != null)
			{
				option |= ModelExporter.ModelExportOption.ConvertToTPose;
				if (!_settings.StraightenElbows)
					option |= ModelExporter.ModelExportOption.KeepElbowBend;
			}

			if (skeleton != null && !string.IsNullOrEmpty(_settings.M2SkeletonPath))
			{
				if (IsBannerlordHuman(skeleton))
				{
					ExportToMedieval2(path, model, skeleton, option, note);
					return string.Join(", ", note);
				}
				note.Add("Medieval 2 transfer skipped: only the human skeleton can be transferred");
			}

			ExportFile(path, model, skeleton, option);

			if (skeleton != null)
				note.Add("rigged to " + skeleton.Name);
			var tPose = ModelExporter.LastTPoseResult;
			if (tPose != null)
				note.Add(tPose.HasChanges ? "T-pose" : "T-pose skipped (" + string.Join("; ", tPose.Log) + ")");
			return note.Count > 0 ? string.Join(", ", note) : null;
		}

		private void ExportFile(string path, Metamesh model, Skeleton skeleton, ModelExporter.ModelExportOption option)
		{
			if (_settings.ModelExtension == "fbx" && AssimpModelExporter.IsAssimpAvailable())
				AssimpModelExporter.ExportToFile(path, model, skeleton, option);
			else
				ModelExporter.ExportToFile(path, model, skeleton, option);
		}

		private static bool IsBannerlordHuman(Skeleton skeleton)
		{
			var names = new HashSet<string>(skeleton.Definition.Data.Bones.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);
			return names.Contains("pelvis") && names.Contains("spine2") && names.Contains("l_clavicle") &&
			       names.Contains("r_clavicle");
		}

		/// <summary>
		/// T-poses the model on its bannerlord skeleton (m2 skeletons are in T-pose), moves it and its weights
		/// onto the m2 skeleton and exports it with the m2 skeleton.
		/// </summary>
		private void ExportToMedieval2(string path, Metamesh model, Skeleton skeleton,
			ModelExporter.ModelExportOption option, List<string> note)
		{
			var withAnimations = _settings.M2IncludeAnimations && _settings.ModelExtension == "fbx" &&
			                     AssimpModelExporter.IsAssimpAvailable();
			if (_m2Skeleton == null)
				_m2Skeleton = M2RigTransfer.LoadSkeleton(_settings.M2SkeletonPath, withAnimations, out _m2Animations);
			var meshes = _settings.AllLods ? model.Meshes : model.Meshes.FindAll(m => m.Lod == 0);

			// the m2 space is fixed (see M2RigTransfer): no axis options, and the T-pose is done here
			option &= ~(ModelExporter.ModelExportOption.ConvertToTPose |
			            ModelExporter.ModelExportOption.KeepElbowBend |
			            ModelExporter.ModelExportOption.NegYAxisForward |
			            ModelExporter.ModelExportOption.YAxisUp |
			            ModelExporter.ModelExportOption.LargerSize);

			List<string> log;
			lock (ModelExporter.SyncRoot)
			{
				using (var tPose = TPoseConverter.Apply(skeleton, meshes, _settings.StraightenElbows))
				{
					if (!tPose.Converter.HasChanges)
						note.Add("T-pose skipped (" + string.Join("; ", tPose.Converter.Log) + ")");
					using (M2RigTransfer.Apply(skeleton, _m2Skeleton, meshes, out log))
					{
						if (withAnimations)
							AssimpModelExporter.ExportToFile(path, model, _m2Skeleton, _m2Animations, option);
						else
							ExportFile(path, model, _m2Skeleton, option);
					}
				}
			}
			note.Add($"transferred from {skeleton.Name} to Medieval 2 {_m2Skeleton.Name}" +
			         (withAnimations ? $" with {_m2Animations.Count} animations" : ""));
			note.AddRange(log);
		}

		private Skeleton PickSkeleton(Metamesh model, List<string> note)
		{
			if (_settings.SkeletonMode == GallerySkeletonMode.None)
				return null;

			var meshes = _settings.AllLods ? model.Meshes : model.Meshes.FindAll(m => m.Lod == 0);
			if (meshes.Count == 0 || meshes.All(m => m.SkinDataSize <= 0))
				return null; // static model

			// the exporters write one weight set per mesh: every exported mesh must be skinned
			if (meshes.Any(m => m.SkinDataSize <= 0 || !HasWeights(m)))
			{
				note.Add("exported static: not every mesh is skinned");
				return null;
			}

			Skeleton skeleton;
			if (_settings.SkeletonMode == GallerySkeletonMode.Specific)
			{
				skeleton = _settings.Skeleton;
			}
			else
			{
				var lower = model.Name.ToLowerInvariant();
				var isHorse = HorseKeywords.Any(k => lower.Contains(k));
				skeleton = isHorse ? _settings.HorseSkeleton ?? _settings.HumanSkeleton : _settings.HumanSkeleton;
			}

			if (skeleton == null)
			{
				note.Add("exported static: skeleton not loaded");
				return null;
			}

			// bone indices must exist in the chosen skeleton or the exporter would index out of range
			var boneCount = skeleton.Definition?.Data?.Bones.Count ?? 0;
			if (boneCount == 0 || meshes.Any(m => MaxBoneIndex(m) >= boneCount))
			{
				note.Add($"exported static: weights don't fit {skeleton.Name}");
				return null;
			}
			return skeleton;
		}

#pragma warning disable 612
		private static bool HasWeights(Mesh mesh)
		{
			var data = mesh.EditData?.Data;
			return data?.Bones != null && data.Positions != null && data.Bones.Length >= data.Positions.Length;
		}

		private static int MaxBoneIndex(Mesh mesh)
		{
			var bones = mesh.EditData?.Data?.Bones;
			if (bones == null)
				return -1;
			int max = -1;
			foreach (var b in bones)
			{
				if (b.W0 > 0) max = Math.Max(max, b.B0);
				if (b.W1 > 0) max = Math.Max(max, b.B1);
				if (b.W2 > 0) max = Math.Max(max, b.B2);
				if (b.W3 > 0) max = Math.Max(max, b.B3);
			}
			return max;
		}
#pragma warning restore 612

		private string ExportMaterial(Material material)
		{
			var name = UniqueName(material.Name);
			var dir = Path.Combine(_settings.OutputDir, _settings.OneFolderPerAsset ? name : "materials\\" + name);
			int count = 0, missing = 0;
			foreach (var dependence in material.Textures.Values)
			{
				if (!dependence.TryGetItem(out var texture) || !texture.HasPixelData)
				{
					missing++;
					continue;
				}
				Directory.CreateDirectory(dir);
				TextureExporter.ExportToFile(Path.Combine(dir, SafeName(texture.Name) + "." + TextureExtension(texture)), texture);
				count++;
			}
			if (count == 0)
				throw new InvalidOperationException("material has no exportable textures");
			return $"{count} textures" + (missing > 0 ? $", {missing} missing" : "");
		}

		private string ExportTexture(Texture texture)
		{
			if (!texture.HasPixelData)
				throw new InvalidOperationException("texture has no pixel data");
			var dir = Path.Combine(_settings.OutputDir, "textures");
			Directory.CreateDirectory(dir);
			var name = UniqueName(texture.Name);
			TextureExporter.ExportToFile(Path.Combine(dir, name + "." + TextureExtension(texture)), texture);
			return null;
		}

		private string TextureExtension(Texture texture)
		{
			MaterialExporter.MaterialExportOption option = 0;
			if (_settings.TexturePreference == 1)
				option = MaterialExporter.MaterialExportOption.PreferPng;
			else if (_settings.TexturePreference == 2)
				option = MaterialExporter.MaterialExportOption.PreferDds;
			return MaterialExporter.GetBestTextureFormat(texture.Format, option);
		}

		private string UniqueName(string name)
		{
			var safe = SafeName(name);
			var result = safe;
			for (int i = 2; !_usedNames.Add(result); i++)
				result = safe + "_" + i;
			return result;
		}

		private static string SafeName(string name)
		{
			if (string.IsNullOrWhiteSpace(name))
				return "unnamed";
			var invalid = Path.GetInvalidFileNameChars();
			return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
		}
	}
}
