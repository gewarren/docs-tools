import { wait } from "./wait";
import { isSuccessStatus } from "./status-checker";
import { setFailed } from "@actions/core";
import { tryUpdatePullRequestBody } from "./pull-updater";
import { workflowInput } from "./types/WorkflowInput";

async function run(): Promise<void> {
  try {
    const token: string = workflowInput.repoToken;

    // Wait 60 seconds before checking status check result.
    await wait(60000);
    console.log("Waited 60 seconds.");

    // When the status is passed, try to update the PR body.
    const isSuccess = await isSuccessStatus(token);
    if (isSuccess && workflowInput.mode === "preview") {
      await tryUpdatePullRequestBody(token);
    } else {
      console.log("Unsuccessful status detected.");
    }
  } catch (error: unknown) {
    const e = error as Error;
    setFailed(e.message);
  }
}

run();
