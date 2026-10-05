# Branching and release workflow

| Branch | Purpose | Who merges | Deploys? |
|---|---|---|---|
| `main` | Production-ready code. Every commit on `main` is a release. | Pull request from `develop` (or `hotfix/*`) only | **Yes** - `cd.yml` tests, then deploys to Render |
| `develop` | Integration branch. All finished features land here first. | Pull request from `feature/*` or `fix/*` | No (CI only) |
| `feature/<short-name>` | One new feature, e.g. `feature/lead-import-preview` | Branch from `develop`, PR back into `develop` | No (CI only) |
| `fix/<short-name>` | Non-urgent bug fix | Branch from `develop`, PR into `develop` | No (CI only) |
| `hotfix/<short-name>` | Urgent production fix | Branch from `main`, PR into `main`, then merge `main` back into `develop` | Yes, on merge to `main` |

## Day-to-day flow

```bash
git checkout develop && git pull
git checkout -b feature/my-change
# ... commit small, meaningful changes ...
git push -u origin feature/my-change      # CI runs automatically
# open a pull request into develop; merge when CI is green and the review is done
```

Release: open a pull request `develop` -> `main`. When it is merged, the **Deploy** workflow runs the tests again, triggers the
Render deploy and waits for `/healthz` to return 200.

## Branch protection (GitHub -> Settings -> Branches)

Apply to both `main` and `develop`:

* Require a pull request before merging
* Require status checks to pass: **Build and test** and **Container image builds**
* Require branches to be up to date before merging
* Block force pushes and deletion

## Commit messages

Short imperative summary, e.g. `Scope the dashboard to the signed-in rep`. Reference the issue number when there is one.

## What the pipelines do

| Workflow | Trigger | Steps |
|---|---|---|
| `ci.yml` | push to `develop` / `feature/**` / `fix/**` / `hotfix/**`, any pull request into `develop` or `main` | restore, build, run unit + integration tests with coverage, upload results, build the Docker image |
| `cd.yml` | push to `main` (or manual run) | run `ci.yml`, call the Render deploy hook, poll `/healthz`, smoke-test the login page |
