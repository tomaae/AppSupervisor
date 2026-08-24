import streamDeck, { SingletonAction } from "@elgato/streamdeck";
import { AppSupervisorPipeServer } from "./status-protocol.js";

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

const pipeServer = new AppSupervisorPipeServer({
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

    if (pipeServer.status !== undefined) {
      await showStatus(event.action, pipeServer.status);
    } else {
      await showOffline(event.action);
    }
  }

  onWillDisappear(event) {
    visibleActions.delete(event.action.id);
  }

  async onKeyDown(event) {
    if (!pipeServer.openConfiguration()) {
      await event.action.showAlert();
    }
  }
}

streamDeck.actions.registerAction(new AppSupervisorStatusAction());
streamDeck.system.onApplicationDidTerminate(() => {
  void updateAll(showOffline);
});
pipeServer.start();
streamDeck.connect();
