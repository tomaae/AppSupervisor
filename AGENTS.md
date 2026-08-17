# AppSupervisor Repository Instructions

## Repository ownership and workflow

- This repository is developed and modified exclusively by Codex in the Codex app on the user's behalf.
- Pull requests are disabled for this project. Do not create, open, propose, recommend, or prepare a pull request.
- Work directly in the local repository and use the workflow explicitly requested by the user.
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
