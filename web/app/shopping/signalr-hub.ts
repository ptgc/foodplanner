import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import type { HubLike } from "./realtime";

/** The only module that imports SignalR (about 15 KB gzipped): `defaultHubFactory` loads it on the first `start()`, so the page renders from the REST list without it (T-128). */
export function buildHubConnection(path: string): HubLike {
  return new HubConnectionBuilder()
    .withUrl(path)
    .withAutomaticReconnect()
    .configureLogging(LogLevel.None)
    .build() as unknown as HubLike;
}
