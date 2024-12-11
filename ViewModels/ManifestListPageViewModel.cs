﻿using System.Collections.Generic;
using CN_GreenLumaGUI.Messages;
using CN_GreenLumaGUI.Models;
using CN_GreenLumaGUI.Pages;
using CN_GreenLumaGUI.tools;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System;
using System.Diagnostics.Metrics;
using Newtonsoft.Json;

namespace CN_GreenLumaGUI.ViewModels
{
	public class ManifestListPageViewModel : ObservableObject
	{
		readonly ManifestListPage page;
		public ManifestListPageViewModel(ManifestListPage page)
		{
			this.page = page;
			this.manifestList = null;
			ScanManifestListCmd = new RelayCommand(ScanManifestList);
			WeakReferenceMessenger.Default.Register<ManifestListChangedMessage>(this, (r, m) =>
			{
				OnPropertyChanged(nameof(PageEndText));
			});
			WeakReferenceMessenger.Default.Register<PageChangedMessage>(this, (r, m) =>
			{
				if (m.toPageIndex == 3)
				{
					if (!isProcess && ManifestList is null && !string.IsNullOrEmpty(DataSystem.Instance.SteamPath))
						ScanManifestList();
					OnPropertyChanged(nameof(PageEndText));
				}
			});
		}
		// Window
		public string ScrollBarEchoState
		{
			get
			{
				if (DataSystem.Instance.ScrollBarEcho)
					return "Visible";
				return "Hidden";
			}
		}
		//Scan
		private bool isProcess = false;
		private int fileTotal = -1;
		public int FileTotal
		{
			get => fileTotal;
			set
			{
				fileTotal = value;
				OnPropertyChanged(nameof(LoadingBarValue));
				OnPropertyChanged(nameof(LoadingBarText));
			}
		}
		private int fileDone = 0;
		public int FileDone
		{
			get => fileDone;
			set
			{
				fileDone = value;
				OnPropertyChanged(nameof(LoadingBarValue));
				OnPropertyChanged(nameof(LoadingBarText));
			}
		}
		public RelayCommand ScanManifestListCmd { get; set; }
		private async void ScanManifestList()
		{
			if (isProcess) return;
			isProcess = true;
			FileTotal = -1;
			FileDone = 0;
			LoadingBarVis = Visibility.Visible;
			(bool result, string reason) = await ScanFromSteam();
			LoadingBarVis = Visibility.Collapsed;
			FileTotal = 0;
			FileDone = 0;
			isProcess = false;
			if (!result)
				ManagerViewModel.Inform($"获取清单失败: {reason}");
			else
				WeakReferenceMessenger.Default.Send(new ManifestListChangedMessage(-1));
		}
		private async Task<(bool, string)> ScanFromSteam()
		{
			if (string.IsNullOrEmpty(DataSystem.Instance.SteamPath))
			{
				return (false, "Steam路径未设置，请在设置页面设置Steam路径。");
			}
			var steamPath = Path.GetDirectoryName(DataSystem.Instance.SteamPath);
			if (string.IsNullOrEmpty(steamPath))
				return (false, "Steam路径错误。");
			var res = await Task.Run(async () =>
			{
				await Task.Yield();
				// 检查网络
				bool hasNetWork = true;
				(_, SteamWebData.GetAppInfoState searchState) = await SteamWebData.Instance.GetAppInformAsync($"https://store.steampowered.com/app/228980/");
				if (searchState == SteamWebData.GetAppInfoState.WrongNetWork) hasNetWork = false;
				// 获取游戏列表
				var gameData = DataSystem.Instance.GetGameDatas();
				Dictionary<long, AppModelLite>? searchAppCache = null;
				try
				{
					if (File.Exists(DataSystem.gameInfoCacheFile))
					{
						var cacheStr = await File.ReadAllTextAsync(DataSystem.gameInfoCacheFile);
						searchAppCache = cacheStr.FromJSON<Dictionary<long, AppModelLite>>();
					}
				}
				catch
				{
					// ignored
				}
				searchAppCache ??= new();
				Dictionary<long, object> dict = new();
				foreach (var game in gameData)
				{
					var gameCopy = new ManifestGameObj(game.GameName, game.GameId);
					dict.Add(game.GameId, gameCopy);
					foreach (var dlc in game.DlcsList)
					{
						dict.Add(dlc.DlcId, dlc);
					}
				}
				foreach (var gamePair in SteamAppFinder.Instance.GameInstall)
				{
					var gameAddName = gamePair.Value;
					if (dict.Remove(gamePair.Key, out var obj))
					{
						if (obj is ManifestGameObj game)
							gameAddName = game.GameName;
					}
					var gameCopy = new ManifestGameObj(gameAddName, gamePair.Key)
					{
						findSelf = true
					};
					dict.Add(gamePair.Key, gameCopy);
				}
				HashSet<long> depotIdExists = new();
				string depotCachePath = Path.Combine(steamPath, "depotcache");
				if (Directory.Exists(depotCachePath))
				{
					var files = Directory.GetFiles(depotCachePath);
					lock (this)
					{
						FileTotal = files.Length;
					}
					foreach (string file in files)
					{
						if (file.EndsWith(".manifest"))
						{
							var name = Path.GetFileNameWithoutExtension(file);
							var cuts = name.Split('_');
							if (cuts.Length != 2) continue;
							var depotId = long.Parse(cuts[0]);
							async Task<(ManifestGameObj?, int)> TryAdd(long localDepotId)
							{
								if (localDepotId < 230000) return (null, 1);
								if (SteamAppFinder.Instance.Excluded.Contains(localDepotId)) return (null, 2);
								if (!depotIdExists.Add(localDepotId)) return (null, 3);
								var appid = localDepotId / 10 * 10;
								if (SteamAppFinder.Instance.Excluded.Contains(appid)) return (null, 4);
								if (dict.TryGetValue(appid, out var app))
								{
									if (app is ManifestGameObj game)
									{
										if (game.GameId != localDepotId)
											game.DepotList!.Add(new(game.GameName, localDepotId, game));
										else
											game.findSelf = true;
										return (game, 0);
									}
									if (app is DlcObj dlc)
									{
										var master = dict[dlc.Master!.GameId] as ManifestGameObj;
										master?.DepotList!.Add(new(dlc.DlcName, localDepotId, master));
										return (master, 0);
									}
									return (null, 5);
								}
								else
								{
									long parentId = 0;
									string echoName = "";
									// 云端查找缓存
									if (!searchAppCache.TryGetValue(appid, out AppModelLite? appInfo))
									{
										// 本地查找
										if (SteamAppFinder.Instance.FindGameByDepotId.TryGetValue(localDepotId, out var pGameId))
											parentId = pGameId;
										// 网络查找
										if (hasNetWork)
										{
											var storeUrl = $"https://store.steampowered.com/app/{appid}/";
											(AppModel? oriInfo, SteamWebData.GetAppInfoState err) = await SteamWebData.Instance.GetAppInformAsync(storeUrl);
											appInfo = oriInfo?.ToLite();
											if (appInfo is not null) searchAppCache.Add(appid, appInfo);
										}
									}
									if (appInfo is not null)
									{
										if (parentId == 0) parentId = appInfo.ParentId;
										echoName = appInfo.AppName;
									}
									if (parentId > 0 && parentId != localDepotId)
									{
										await TryAdd(parentId);
										var master = dict[parentId] as ManifestGameObj;
										master?.DepotList!.Add(new(echoName, localDepotId, master));
										return (master, 5);
									}
									var game = new ManifestGameObj(echoName, appid);
									if (game.GameId != localDepotId)
										game.DepotList!.Add(new(game.GameName, localDepotId, game));
									else
										game.findSelf = true;
									dict.Add(appid, game);
									return (game, 0);
								}
							}
							await TryAdd(depotId);
						}
						lock (this)
						{
							FileDone++;
						}
					}
				}
				await File.WriteAllTextAsync(DataSystem.gameInfoCacheFile, JsonConvert.SerializeObject(searchAppCache));
				ObservableCollection<ManifestGameObj> newList = new();
				foreach (var item in dict.Values)
				{
					if (item is not ManifestGameObj game) continue;
					if (game is { findSelf: false, DepotList.Count: 0 }) continue;
					newList.Add(game);
				}
				return newList;
			});
			ManifestList?.Clear();
			ManifestList = res;
			return (true, string.Empty);
		}

		//Binding

		private ObservableCollection<ManifestGameObj>? manifestList;

		public ObservableCollection<ManifestGameObj>? ManifestList
		{
			get
			{
				lock (this)
				{
					return manifestList;
				}
			}
			set
			{
				lock (this)
				{
					manifestList = value;
				}
				OnPropertyChanged();
			}
		}
		private Visibility loadingBarVis = Visibility.Collapsed;
		public Visibility LoadingBarVis
		{
			get { return loadingBarVis; }
			set
			{
				loadingBarVis = value;
				OnPropertyChanged();
			}
		}
		public string LoadingBarText
		{
			get
			{
				if (FileTotal == 0) return "扫描完成";
				return $"{FileDone}/{FileTotal} 正在扫描";
			}
		}
		public int LoadingBarValue
		{
			get
			{
				if (FileTotal < 0) return 0;
				if (FileTotal == 0) return 100;
				return (int)(FileDone * 100.0 / FileTotal);
			}
			set
			{

			}
		}

		public string PageEndText
		{
			get
			{
				if (DataSystem.Instance.SteamPath is null or "")
				{
					return "Steam路径未设置，请在设置页面设置Steam路径。";
				}
				int count = ManifestList?.Count ?? -1;
				if (count == -1)
					return "正在扫描本地清单……";
				if (count == 0)
					return "未找到清单，可能Steam未登录或未下载过游戏。";
				if (count > 12)
					return "没有更多了……";
				return "";
			}
		}
	}
}
