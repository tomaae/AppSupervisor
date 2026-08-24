import streamDeck, { SingletonAction } from "@elgato/streamdeck";
import { AppSupervisorPipeClient } from "./status-protocol.js";

const ACTION_UUID = "com.tomaae.appsupervisor.status";
const visibleActions = new Map();

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
    [...visibleActions.values()].map((action) => render(action)),
  );

  for (const result of results) {
    if (result.status === "rejected") {
      streamDeck.logger.error("Could not update an AppSupervisor status action.", result.reason);
    }
  }
}

const pipeClient = new AppSupervisorPipeClient({
  onStatus: (status) => {
    void updateAll((action) => showStatus(action, status));
  },
  onConnectionChanged: (connected) => {
    if (!connected) {
      void updateAll(showOffline);
    }
  },
});

class AppSupervisorStatusAction extends SingletonAction {
  manifestId = ACTION_UUID;

  async onWillAppear(event) {
    visibleActions.set(event.action.id, event.action);

    if (pipeClient.status !== undefined) {
      await showStatus(event.action, pipeClient.status);
    } else {
      await showOffline(event.action);
    }
  }

  onWillDisappear(event) {
    visibleActions.delete(event.action.id);
  }

  async onKeyDown(event) {
    if (!pipeClient.openConfiguration()) {
      await event.action.showAlert();
    }
  }
}

streamDeck.actions.registerAction(new AppSupervisorStatusAction());
streamDeck.system.onApplicationDidLaunch(() => pipeClient.connectNow());
streamDeck.system.onApplicationDidTerminate(() => {
  void updateAll(showOffline);
});
pipeClient.start();
streamDeck.connect();
