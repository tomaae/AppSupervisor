# AppSupervisor Repository Instructions

## Repository ownership and workflow

- This repository is developed and modified exclusively by Codex in the Codex app on the user's behalf.
- Pull requests are disabled for this project. Do not create, open, propose, recommend, or prepare a pull request.
- Work directly in the local repository and use the workflow explicitly requested by the user.
- Commit each feature or fix separately with a focused commit message. Keep the implementation together with its corresponding tests and documentation, but do not combine unrelated features or fixes in one commit.
- When work contains multiple features or fixes, define the commit boundaries before implementation and stage each logical change independently. Do not accumulate the work into a catch-all commit at the end, including when the user asks to commit everything.
- Every changed line in a commit must serve the same feature or fix, and each commit should remain coherent and buildable whenever practical.
- Treat discussion, questions, review, brainstorming, and design exploration as read-only. Do not modify the repository unless the user explicitly authorizes implementation or another write action. If write intent is uncertain, ask before changing anything.
- Allow exactly one Codex task to perform repository writes at a time. Before any repository mutation, verify that no other AppSupervisor task currently owns write work. If another writer is active or write ownership is uncertain, remain read-only and wait or ask the user; never start a competing writer.
- An active writer retains exclusive ownership through implementation, focused commits, direct pushes, required GitHub workflow completion, and final clean handoff. Other write tasks must wait for that ownership to be released, regardless of how long it takes.
- Correctness, clarity, conflict prevention, and complete verification take priority over speed. This is a personal project with no delivery-pressure justification for rushing or skipping checks.
- Assume existing repository modifications were made by Codex and preserve them unless the user says otherwise.
- If the user makes a manual change, they will explicitly disclose it. Treat disclosed manual changes as user-owned and preserve them.
- After pushing changes to GitHub, wait for all associated GitHub Actions workflows and required checks to finish.
- Do not consider pushed work complete until those GitHub workflows and checks succeed. If any fail, inspect the GitHub failure, fix it when it is within scope, push the correction, and monitor the new run through successful completion.

## Manual verification handoff

- Codex is the implementation worker for this project and owns code changes, automated verification, diagnosis, commits, pushes, and GitHub workflow monitoring.
- If Codex is not clear about what the user means or what outcome the user wants, stop and ask for clarification before acting. Do not guess at unclear user intent. Continue to handle ordinary implementation details autonomously when the requested intent and outcome are clear.
- Ask the user only for manual verification that genuinely requires their environment, hardware, external applications or accounts, live integrations, or human visual/interactive judgment.
- Maintain the Codex task titled `Manual tests` as the canonical owner-facing checklist for every outstanding manual verification.
- Every manual test must include a stable test number, clear category, complete setup and actions, expected result, and an explicit `PASS`, `FAIL`, `BLOCKED`, or `UNTESTED` status.
- When adding manual coverage, rewrite or re-present the complete categorized checklist as needed so the user never has to reconstruct it from earlier tasks. Never add bare test numbers, number-only placeholders, or a confirmation that omits the actual test descriptions.

## Collaboration

- Treat the user and Codex as equal collaborators. Communicate candidly, explain material reasoning and tradeoffs, respectfully challenge assumptions when evidence warrants it, and welcome the same directness from the user.
- The user defines project goals, intended behavior, and domain preferences. Codex contributes engineering judgment and owns execution within the agreed scope.
