using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using JetBrains.Annotations;
using TpacTool.Lib;

namespace TpacTool.IO
{
	public static class ModelExporter
	{
		public static void ExportToFile([NotNull] string path, [CanBeNull] Metamesh model,
			[CanBeNull] Skeleton skeleton = null, ModelExportOption option = 0)
		{
			ExportToFile(path, model, skeleton, null, null, option);
		}

		public static void ExportToFile([NotNull] string path, [CanBeNull] Metamesh model,
			[CanBeNull] Skeleton skeleton = null, 
			[CanBeNull] SkeletalAnimation animation = null, [CanBeNull] MorphAnimation morph = null,
			ModelExportOption option = 0)
		{
			if (path.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
				ExportToFile<WavefrontExporter>(path, model, skeleton, animation, morph, option);
			else if (path.EndsWith(".dae", StringComparison.OrdinalIgnoreCase))
				ExportToFile<ColladaExporter>(path, model, skeleton, animation, morph, option);
			else
				throw new FormatException("Unsupported export format");
		}

		public static void ExportToFile<T>([NotNull] string path, [CanBeNull] Metamesh model,
			[CanBeNull] Skeleton skeleton = null, ModelExportOption option = 0)
			where T : AbstractModelExporter, new()
		{
			ExportToFile<T>(path, model, skeleton, null, null, option);
		}

		public static void ExportToFile<T>([NotNull] string path, [CanBeNull] Metamesh model,
			[CanBeNull] Skeleton skeleton = null, 
			[CanBeNull] SkeletalAnimation animation = null, [CanBeNull] MorphAnimation morph = null,
			ModelExportOption option = 0)
			where T : AbstractModelExporter, new()
		{
			T exporter = new T();
			ExportToFile(exporter, path, model, skeleton, animation, morph, option);
		}

		public static void ExportToFile([NotNull] AbstractModelExporter exporter, [NotNull] string path, [CanBeNull] Metamesh model,
			[CanBeNull] Skeleton skeleton = null,
			[CanBeNull] SkeletalAnimation animation = null, [CanBeNull] MorphAnimation morph = null,
			ModelExportOption option = 0, [CanBeNull] IEnumerable<SkeletalAnimation> animations = null,
			[CanBeNull] IEnumerable<string> animationNames = null,
			[CanBeNull] IEnumerable<Tuple<float, float>> animationFrameRanges = null)
		{
			// exports are serialized: a T-pose export temporarily re-poses the shared skeleton and meshes,
			// so a concurrent export (e.g. gallery batch + model page) must never observe or save that state
			lock (ExportLock)
			{
				ExportToFileLocked(exporter, path, model, skeleton, animation, morph, option, animations,
					animationNames, animationFrameRanges);
			}
		}

		private static readonly object ExportLock = new object();

		/// <summary>
		/// Held by every export. Callers that re-pose shared skeletons / meshes themselves before calling
		/// an exporter (e.g. a rig transfer) lock it around the whole operation.
		/// </summary>
		public static object SyncRoot => ExportLock;

		private static void ExportToFileLocked(AbstractModelExporter exporter, string path, Metamesh model,
			Skeleton skeleton, SkeletalAnimation animation, MorphAnimation morph, ModelExportOption option,
			IEnumerable<SkeletalAnimation> animations, IEnumerable<string> animationNames,
			IEnumerable<Tuple<float, float>> animationFrameRanges)
		{
			if (model == null)
				model = Metamesh.EmptyMesh;

			var dirPath = Path.GetDirectoryName(path) + "/";

			var exportedMeshes = model.Meshes;
			var lodMask = int.MaxValue;
			if (!option.HasFlag(ModelExportOption.ExportAllLod))
			{
				lodMask = 1;
				exportedMeshes = exportedMeshes.FindAll(mesh => mesh.Lod == 0);
			}
			HashSet<Texture> textures = new HashSet<Texture>();
			foreach (var mesh in exportedMeshes)
			{
				if (mesh.Material.TryGetItem(out var mat1))
				{
					foreach (var texDep in mat1.Textures.Values)
					{
						if (texDep.TryGetItem(out var tex))
							textures.Add(tex);
					}
				}

				// second material textures are always written next to the model, even when the
				// format can't reference them, so the export contains every associated texture
				if (mesh.SecondMaterial.TryGetItem(out var mat2))
				{
					foreach (var texDep in mat2.Textures.Values)
					{
						if (texDep.TryGetItem(out var tex))
							textures.Add(tex);
					}
				}
			}

			string prefix = option.HasFlag(ModelExportOption.ExportTexturesSubFolder)
				? model.Name + "/"
				: string.Empty;
			foreach (var tex in textures)
			{
				var texRelPath = prefix + tex.Name + "." + GetTextureFormat(tex.Format, option);
				exporter.TexturePathMapping[tex] = texRelPath;
				var texFullPath = dirPath + texRelPath;
				if (tex.HasPixelData && (option.HasFlag(ModelExportOption.ExportTextures) ||
										option.HasFlag(ModelExportOption.ExportTexturesSubFolder)))
					TextureExporter.ExportToFile(texFullPath, tex);
			}

			TPoseScope tPose = null;
			if (option.HasFlag(ModelExportOption.ConvertToTPose) && skeleton != null)
				tPose = TPoseConverter.Apply(skeleton, exportedMeshes,
					!option.HasFlag(ModelExportOption.KeepElbowBend));
			LastTPoseResult = tPose?.Converter;

			try
			{
				exporter.Model = model;
				exporter.Skeleton = skeleton;
				exporter.Animation = animation;
				exporter.Animations = animations?.ToList();
				exporter.AnimationNames = animationNames?.ToList();
				exporter.AnimationFrameRanges = animationFrameRanges?.ToList();
				exporter.Morph = morph;
				exporter.LodMask = lodMask;
				exporter.FixBoneForBlender = option.HasFlag(ModelExportOption.FixBoneForBlender);
				exporter.IsNegYAxisForward = option.HasFlag(ModelExportOption.NegYAxisForward);
				exporter.IsYAxisUp = option.HasFlag(ModelExportOption.YAxisUp);
				exporter.IsLargerSize = option.HasFlag(ModelExportOption.LargerSize);
				exporter.IsDiffuseOnly = option.HasFlag(ModelExportOption.ExportDiffuseOnly);
				exporter.Export(path);
			}
			finally
			{
				tPose?.Dispose();
			}
		}

		/// <summary>
		/// The T-pose solve of the most recent export on this thread (null when T-pose was off or there
		/// was no skeleton). Useful to report which bones were adjusted.
		/// </summary>
		[ThreadStatic]
		public static TPoseConverter LastTPoseResult;

		private static string GetTextureFormat(TextureFormat format, ModelExportOption option)
		{
			// the two enums don't share bit values, so translate instead of casting
			MaterialExporter.MaterialExportOption materialOption = 0;
			if (option.HasFlag(ModelExportOption.PreferPng))
				materialOption |= MaterialExporter.MaterialExportOption.PreferPng;
			else if (option.HasFlag(ModelExportOption.PreferDds))
				materialOption |= MaterialExporter.MaterialExportOption.PreferDds;
			return MaterialExporter.GetBestTextureFormat(format, materialOption);
		}

		/*public static bool CheckAssimpInited()
		{
			return AssimpLibrary.Instance.IsLibraryLoaded;
		}*/

		[Flags]
		public enum ModelExportOption
		{
			LargerSize = 0x1,
			YAxisUp = 0x2,
			NegYAxisForward = 0x4,
			ExportTextures = 0x1000,
			ExportTexturesSubFolder = 0x2000,
			ExportDiffuseOnly = 0x4000,
			FixBoneForBlender = 0x10000,
			PreferPng = 0x20000,
			PreferDds = 0x40000,
			ExportAllLod = 0x100000,
			// re-pose the skeleton (and skinned meshes) from the authored A-pose to a T-pose
			ConvertToTPose = 0x1000000,
			// with ConvertToTPose: only swing the upper arms, keep the forearms' elbow bend
			KeepElbowBend = 0x2000000
		}
	}
}