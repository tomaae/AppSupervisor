import streamDeck, { SingletonAction } from "@elgato/streamdeck";
import { AppSupervisorPipeServer } from "./status-protocol.js";

const STATUS_ACTION_UUID = "com.tomaae.appsupervisor.status";
const LAUNCH_ACTION_UUID = "com.tomaae.appsupervisor.launch-profile";
const visibleStatusActions = new Map();
const visibleLaunchActions = new Map();
let launchPropertyInspectorVisible = false;

async function showOffline(action) {
  await Promise.all([action.setImage(), action.setTitle("Offline")]);
}

async function showStatus(action, status) {
  await Promise.all([
    action.setImage(status.image),
    action.setTitle(status.title),
  ]);
}

async function updateAll(render) {
  const results = await Promise.allSettled(
    [...visibleStatusActions.values()].map((action) => render(action)),
  );

  for (const result of results) {
    if (result.status === "rejected") {
      streamDeck.logger.error("Could not update an AppSupervisor status action.", result.reason);
    }
  }
}

function getSelectedProfile(status, settings) {
  const profileId = settings?.profileId;
  return status?.profiles.find((profile) => profile.id === profileId);
}

async function showLaunchAction(action, settings) {
  if (!pipeServer.connected) {
    await Promise.all([
      action.setImage("static/imgs/keys/offline"),
      action.setTitle("Offline"),
    ]);
    return;
  }

  const profile = getSelectedProfile(pipeServer.status, settings);
  await Promise.all([
    action.setImage(),
    action.setTitle(profile?.name ?? "Select\nprofile"),
  ]);
}

async function updateLaunchActions() {
  const results = await Promise.allSettled(
    [...visibleLaunchActions.values()].map(({ action, settings }) =>
      showLaunchAction(action, settings)),
  );

  for (const result of results) {
    if (result.status === "rejected") {
      streamDeck.logger.error("Could not update an AppSupervisor launch action.", result.reason);
    }
  }
}

async function sendProfilesToPropertyInspector() {
  if (!launchPropertyInspectorVisible) {
    return;
  }

  try {
    await streamDeck.ui.sendToPropertyInspector({
      type: "profiles",
      connected: pipeServer.connected,
      profiles: pipeServer.status?.profiles ?? [],
    });
  } catch (error) {
    streamDeck.logger.error(
      "Could not update the AppSupervisor launch property inspector.",
      error,
    );
  }
}

const pipeServer = new AppSupervisorPipeServer({
  onStatus: (status) => {
    void updateAll((action) => showStatus(action, status));
    void updateLaunchActions();
    void sendProfilesToPropertyInspector();
  },
  onConnectionChanged: (connected) => {
    if (!connected) {
      void updateAll(showOffline);
    }
    void updateLaunchActions();
    void sendProfilesToPropertyInspector();
  },
});

class AppSupervisorStatusAction extends SingletonAction {
  manifestId = STATUS_ACTION_UUID;

  async onWillAppear(event) {
    visibleStatusActions.set(event.action.id, event.action);

    if (pipeServer.status !== undefined) {
      await showStatus(event.action, pipeServer.status);
    } else {
      await showOffline(event.action);
    }
  }

  onWillDisappear(event) {
    visibleStatusActions.delete(event.action.id);
  }

  async onKeyDown(event) {
    if (!pipeServer.openConfiguration()) {
      await event.action.showAlert();
    }
  }
}

class LaunchMonitoredAppAction extends SingletonAction {
  manifestId = LAUNCH_ACTION_UUID;

  async onWillAppear(event) {
    const settings = event.payload.settings ?? {};
    visibleLaunchActions.set(event.action.id, { action: event.action, settings });
    await showLaunchAction(event.action, settings);
  }

  onWillDisappear(event) {
    visibleLaunchActions.delete(event.action.id);
  }

  async onDidReceiveSettings(event) {
    visibleLaunchActions.set(event.action.id, {
      action: event.action,
      settings: event.payload.settings ?? {},
    });
    await showLaunchAction(event.action, event.payload.settings ?? {});
  }

  async onKeyDown(event) {
    const profile = getSelectedProfile(pipeServer.status, event.payload.settings);
    if (profile === undefined || !pipeServer.launchProfile(profile.id)) {
      await event.action.showAlert();
      return;
    }

    await event.action.showOk();
  }

  async onPropertyInspectorDidAppear() {
    launchPropertyInspectorVisible = true;
    await sendProfilesToPropertyInspector();
  }

  onPropertyInspectorDidDisappear() {
    launchPropertyInspectorVisible = false;
  }

  async onSendToPlugin() {
    await sendProfilesToPropertyInspector();
  }
}

streamDeck.actions.registerAction(new AppSupervisorStatusAction());
streamDeck.actions.registerAction(new LaunchMonitoredAppAction());
pipeServer.start();
streamDeck.connect();
