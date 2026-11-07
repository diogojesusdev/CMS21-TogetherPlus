using System;

namespace CMS21Together.Shared;

[Serializable]
public enum PacketTypes
{
	connect,
	playerLeft,
	identityRequest,
	userData,
	readyState,
	start,

	spawn,
	position,
	rotation,
	item,
	groupItem,
	lifter,
	loadJobCar,
	loadCar,
	bodyPart,
	partScript,
	deleteCar,
	carPosition,
	garageUpgrade,
	newJob,
	jobAction,
	selectedJob,
	sceneChange,
	contentInfo,
	toolMove,
	setSpringClamp,
	clearSpringClamp,
	setTireChanger,
	clearTireChanger,
	wheelBalance,
	endJob,
	oilBinUse,
	engineCrane,
	skillChange,
	engineStandAngle,
	engineStandSetGroup,
	engineStandTakeOff,
	carFluid,
	exp,
	point,
	resync,
	carWash,
	carPaint,
	useWelder,
	repairPart,
	parkAdd,
	parkRemove,
	playerInCar,
	carEngineSound,

	lockRequest,
	lockRenew,
	lockRelease,
	lockReply,
	lockState,
	lockSnapshot,

	econTxn,
	econBalance,

	replicaBegin,
	replicaChunk,
	replicaAck,
	replicaCancel,
	replicaOffer,
	replicaRequest,

	invPush,
	invOp,
	invSnapshot,
	invRequest,
	invCheck,

	purchaseAsk,
	purchaseAnswer,

	carCheck,
	statsFix,
	features,
	feature,
	bulkSell,

	/// <summary>A player's item UID counter (a new profile gets no spawn packet; Phase 8 assessment).</summary>
	uidRange,

	/// <summary>The "zengo" cheat chord was pressed: the host credits a fixed amount to the sender.</summary>
	cheatMoney
}