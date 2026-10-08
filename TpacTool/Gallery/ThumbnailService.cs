using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TpacTool
{
	/// <summary>
	/// Generates gallery thumbnails on background STA threads. Requests are served newest-first so the
	/// tiles currently on screen are rendered before ones the user already scrolled past, and only a
	/// bounded number of thumbnails is kept in memory.
	/// </summary>
	internal static class ThumbnailService
	{
		public const int RenderSize = 192;
		private const int MaxQueue = 256;
		private const int MaxCached = 900;

		private static readonly object Lock = new object();
		private static readonly LinkedList<GalleryItem> Queue = new LinkedList<GalleryItem>();
		private static readonly LinkedList<GalleryItem> Cache = new LinkedList<GalleryItem>();
		private static Thread[] _workers;
		private static int _generation;

		public static void Request(GalleryItem item)
		{
			lock (Lock)
			{
				EnsureWorkers();
				if (item.Pending)
					return;
				item.Pending = true;
				Queue.AddFirst(item);
				while (Queue.Count > MaxQueue)
				{
					// forget the oldest requests: those tiles are most likely off screen by now and will
					// ask again when they are shown
					var last = Queue.Last.Value;
					Queue.RemoveLast();
					last.Pending = false;
				}
				Monitor.Pulse(Lock);
			}
		}

		/// <summary>Drops every queued request and cached thumbnail (e.g. when the gallery closes).</summary>
		public static void Clear()
		{
			lock (Lock)
			{
				_generation++;
				foreach (var item in Queue)
					item.Pending = false;
				Queue.Clear();
				foreach (var item in Cache)
					item.DropThumbnail();
				Cache.Clear();
			}
		}

		private static void EnsureWorkers()
		{
			if (_workers != null)
				return;
			var count = Math.Max(1, Math.Min(3, Environment.ProcessorCount / 2));
			_workers = new Thread[count];
			for (int i = 0; i < count; i++)
			{
				var thread = new Thread(WorkerLoop)
				{
					IsBackground = true,
					Name = "Gallery thumbnail worker " + i,
					Priority = ThreadPriority.BelowNormal
				};
				thread.SetApartmentState(ApartmentState.STA);
				_workers[i] = thread;
				thread.Start();
			}
		}

		private static void WorkerLoop()
		{
			while (true)
			{
				GalleryItem item;
				int generation;
				lock (Lock)
				{
					while (Queue.Count == 0)
						Monitor.Wait(Lock);
					item = Queue.First.Value;
					Queue.RemoveFirst();
					generation = _generation;
				}

				BitmapSource bitmap = null;
				bool failed = false;
				try
				{
					bitmap = ThumbnailRenderer.Render(item.Asset, RenderSize);
					failed = bitmap == null;
				}
				catch (Exception)
				{
					failed = true;
				}

				// release the wpf objects created on this thread
				Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));

				lock (Lock)
				{
					if (generation != _generation)
					{
						item.Pending = false;
						continue;
					}
					if (bitmap != null)
					{
						Cache.AddFirst(item);
						while (Cache.Count > MaxCached)
						{
							Cache.Last.Value.DropThumbnail();
							Cache.RemoveLast();
						}
					}
				}

				var app = Application.Current;
				if (app == null)
					return;
				app.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
				{
					if (generation == _generation)
						item.SetThumbnail(bitmap, failed);
				}));
			}
		}
	}
}
