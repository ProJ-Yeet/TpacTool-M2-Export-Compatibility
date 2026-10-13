using System;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;

namespace TpacTool
{
	/// <summary>
	/// Frees memory before the system runs out. Asset data (textures, meshes) is only weakly referenced
	/// once it has been used, but it stays on the heap until a full garbage collection, and the runtime may
	/// put that off for gigabytes - while another program (e.g. Blender with a big scene) needs the RAM, so
	/// Windows starts paging and the whole machine stalls. This collects as soon as the heap has grown a
	/// lot, or a little when the system is low on physical memory.
	/// </summary>
	internal static class MemoryGuard
	{
		private const long GrowthLimit = 1024L * 1024 * 1024;
		private const long LowMemoryGrowthLimit = 128L * 1024 * 1024;
		private const long LowMemoryFloor = 1536L * 1024 * 1024;
		private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);
		private static readonly TimeSpan MinCollectInterval = TimeSpan.FromSeconds(5);

		private static Timer _timer;
		private static int _busy;
		private static long _heapAfterCollect;
		private static DateTime _lastCollect = DateTime.MinValue;

		public static void Start()
		{
			if (_timer != null)
				return;
			_heapAfterCollect = GC.GetTotalMemory(false);
			_timer = new Timer(_ => Check(), null, CheckInterval, CheckInterval);
		}

		private static void Check()
		{
			if (Interlocked.Exchange(ref _busy, 1) != 0)
				return;
			try
			{
				if (DateTime.UtcNow - _lastCollect < MinCollectInterval)
					return;
				var growth = GC.GetTotalMemory(false) - _heapAfterCollect;
				if (growth > GrowthLimit || (growth > LowMemoryGrowthLimit && IsSystemLowOnMemory()))
					Collect();
			}
			catch (Exception)
			{
				// never take the app down over this
			}
			finally
			{
				Interlocked.Exchange(ref _busy, 0);
			}
		}

		/// <summary>A full, compacting collection; also runs finalizers, which free WPF's native bitmaps.</summary>
		public static void Collect()
		{
			GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			_heapAfterCollect = GC.GetTotalMemory(false);
			_lastCollect = DateTime.UtcNow;
		}

		/// <summary>Less than 1.5 GB or 10% of physical memory is available.</summary>
		private static bool IsSystemLowOnMemory()
		{
			var status = new MEMORYSTATUSEX { dwLength = (uint) Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
			if (!GlobalMemoryStatusEx(ref status))
				return false;
			var floor = Math.Max(LowMemoryFloor, (long) (status.ullTotalPhys / 10));
			return (long) status.ullAvailPhys < floor;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct MEMORYSTATUSEX
		{
			public uint dwLength;
			public uint dwMemoryLoad;
			public ulong ullTotalPhys;
			public ulong ullAvailPhys;
			public ulong ullTotalPageFile;
			public ulong ullAvailPageFile;
			public ulong ullTotalVirtual;
			public ulong ullAvailVirtual;
			public ulong ullAvailExtendedVirtual;
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
	}
}
