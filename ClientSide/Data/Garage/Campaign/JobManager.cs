using System.Collections;
using System.Collections.Generic;
using System.Linq;
using CMS;
using CMS.FileSupport.INI;
using CMS.Tutorial;
using CMS.UI;
using CMS21Together.ClientSide.Data.Garage.Car;
using CMS21Together.ClientSide.Data.Handle;
using CMS21Together.Shared.Data.Vanilla.Jobs;
using MelonLoader;
using UnityEngine;
using CMS21Together.Shared;

namespace CMS21Together.ClientSide.Data.Garage.Campaign;

public static class JobManager
{
	public static List<ModJob> selectedJobs = new();

	public static void Reset()
	{
		selectedJobs.Clear();
	}

	public static IEnumerator SelectedJob(ModJob modjob, bool action)
	{
		while (!GameLoadHook.IsGameReady())
			yield return new WaitForSeconds(0.25f);
		yield return new WaitForEndOfFrame();

		if (action)
		{
			if (selectedJobs.All(j => modjob.id != j.id))
			{
				selectedJobs.Add(modjob);
				GameData.Instance.orderGenerator.selectedJobs.Add(modjob.ToGame());
			}
		}
		else
		{
			if (selectedJobs.Any(j => modjob.id == j.id))
			{
				var gameJobs = GameData.Instance.orderGenerator.selectedJobs;
				selectedJobs.Remove(selectedJobs.First(j => j.id == modjob.id));
				gameJobs.Remove(gameJobs.ToArray().First(j => j.id == modjob.id));
			}
		}
	}

	public static IEnumerator AddJob(ModJob job)
	{
		while (!GameLoadHook.IsGameReady())
			yield return new WaitForSeconds(0.25f);
		yield return new WaitForEndOfFrame();
		
		var newJob = job.ToGame();
		// The host re-sends its board after every garage load and a late join replays it: the same order must not be
		// added twice (a duplicate can never be accepted and inflates the board).
		if (GameData.Instance.orderGenerator.jobs.ToArray().Any(j => j.id == newJob.id))
		{
			Log.Debug($"[JobManager] Order {newJob.id} is already on the board; ignored.");
			yield break;
		}
		newJob.timeToEnd -= 3;
		GameData.Instance.orderGenerator.jobs.Add(newJob);
		if (!newJob.IsMission)
			GameData.Instance.orderGenerator.jobs.ToArray()[GameData.Instance.orderGenerator.jobs.Count-1].StartTimer();
		GlobalData.AddJob(1);
		UIManager.Get().UpdateJobs(GameData.Instance.orderGenerator.jobs, newJob);
		Log.Debug($"Should have added a Mision! {newJob.id} , {newJob.IsMission}");
	}

	public static IEnumerator JobAction(ModJob modJob, bool takeJob)
	{
		while (!GameLoadHook.IsGameReady())
			yield return new WaitForSeconds(0.25f);
		yield return new WaitForEndOfFrame();

		Job job = modJob.ToGame();
		foreach (var _job in GameData.Instance.orderGenerator.jobs.ToArray())
		{
			if (_job.id == modJob.id)
			{
				GameData.Instance.orderGenerator.CancelJob(_job.id);
				break;
			}
		}

		if (takeJob)
		{
			GameData.Instance.orderGenerator.selectedJobs.Add(job);
			UIManager.Get().UpdateJobs(GameData.Instance.orderGenerator.jobs, null);
			Log.Debug($"CL: Took Job! {job.id} , cLoader {job.carLoaderID}");
		}
	}

	public static IEnumerator OnJobComplete(ModJob job)
	{
		while (!GameLoadHook.IsGameReady())
			yield return new WaitForSeconds(0.25f);
		yield return new WaitForEndOfFrame();

		Log.Debug("[JobManager] -> OnJobComplete");
		Log.Debug("\n - Job Info received - " +
		                $"\nID:{job.id}" +
		                $"\nIsMission:{job.IsMission}" +
		                $"\nisCompleted:{job.IsCompleted}" +
		                $"\nPayout:{job.TotalPayout}" +
		                $"\nXP:{job.XP}" +
		                $"\nMoneySpent:{job.MoneySpent}" +
		                "\n----------------------------------------");

		// XP is per player (plan §3.8): only the player who handed the order over earns it.
		Singleton<GameManager>.Instance.OrderGenerator.CancelJob(job.id);

		// The delete is not sent back, and its hook returns before the bookkeeping: forget the car here or the next
		// job car on this loader is never announced (`CarSpawnManager.LoadJobCar` early-returns).
		var loader = job.carLoaderID >= 0 && job.carLoaderID < GameData.Instance.carLoaders.Length
			? GameData.Instance.carLoaders[job.carLoaderID]
			: null;
		if (loader != null)
		{
			ClientData.Instance.loadedCars.Remove(job.carLoaderID);
			CarSpawnHooks.listenToDelete = false;
			try
			{
				loader.DeleteCar();
			}
			finally
			{
				CarSpawnHooks.listenToDelete = true;
			}
		}
		if (job.IsMission)
		{
			GlobalData.IsStoryMissionInProgress = false;
			GlobalData.MissionsFinished++;
			GlobalData.CurrentMissionDone = true;
			if (GlobalData.MissionsFinished >= GlobalData.MissionsAmount) Singleton<GameManager>.Instance.PlatformManager.IncrementStat("stat_finish_allmissions", 1);
		}

		if (selectedJobs.Any(j => j.id == job.id))
		{
			var modJob = selectedJobs.First(j => j.id == job.id);
			selectedJobs.Remove(modJob);
			if (job.IsCompleted) Singleton<GameManager>.Instance.PlatformManager.IncrementStat("stat_finish_order", 1);
			if (job.IsCompleted && job.BonusToExp) Singleton<GameManager>.Instance.PlatformManager.IncrementStat("stat_bonus_exp", 1);
			if (job.IsCompleted && job.BonusToMoney) Singleton<GameManager>.Instance.PlatformManager.IncrementStat("stat_bonus_money", 1);
			Log.Debug("[JobManager] -> OnJobComplete() Finished !");
		}
	}
}