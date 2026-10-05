# Deploying to GitHub and Render

## 1. How Docker works here (plain English)

Docker packs the app and everything it needs (the .NET runtime, your compiled code, config) into one **image**. A running copy of
an image is a **container**. The same image runs identically on your laptop, in GitHub's test machines and on Render, so
"it works on my machine" stops being a problem.

The `Dockerfile` has two stages:

| Stage | Base image | What it does |
|---|---|---|
| `build` | `dotnet/sdk:9.0` (big, has the compiler) | Copies `UncoveringGreatnessCRM.csproj` first and runs `dotnet restore` (this layer is cached, so rebuilds are fast when only code changes), then copies the source and runs `dotnet publish` into `/app/publish`. |
| `final` | `dotnet/aspnet:9.0` (small, runtime only) | Copies only the published output from the build stage. No compiler or source code ends up in the image that is deployed. |

Other lines in the Dockerfile:

* `ENV ASPNETCORE_ENVIRONMENT=Production` turns on production behaviour (secure cookies, no developer error pages).
* `RUN mkdir -p /app/App_Data` is where the SQLite database (`crm.db`) and the login-cookie encryption keys live.
  **A container's own filesystem is wiped on every deploy**, so Render mounts a persistent *disk* on this folder (see `render.yaml`). Without it you would lose all data on each deploy.
* `EXPOSE 10000` and the `CMD` start the app on the port Render gives us in the `PORT` variable.
* `.dockerignore` keeps `bin/`, `obj/`, `Tests/`, `.git/` and local databases out of the image so it stays small and clean.

Try it on your computer (needs Docker Desktop):

```bash
docker compose up --build        # builds the image and starts it; open http://localhost:8080
docker compose down              # stop (data is kept in the crm-data volume)
docker compose down -v           # stop and delete the data
```

## 2. Put the code on GitHub

The folder you were given is already a git repository with its history and branches (`main`, `develop`, `feature/*`).

```bash
# 1. Create an EMPTY private repository on github.com (no README, no .gitignore), copy its URL
git remote add origin https://github.com/<you>/<repo>.git
git push -u origin --all          # pushes main, develop and every feature branch
git log --oneline main | wc -l    # shows how many commits main has (more than 20)
```

Then in GitHub: **Settings > Branches > Add rule** for `main` and `develop` (rules are listed in `docs/BRANCHING.md`), and set
**Settings > Secrets and variables > Actions**: secret `RENDER_DEPLOY_HOOK_URL` (step 3) and variable `APP_URL`.
Create an environment called `production` (Settings > Environments) - the deploy job uses it.

Replace the placeholder author on the commits with yourself (so GitHub shows your name) **before** you push:

```bash
git filter-branch -f --env-filter '
export GIT_AUTHOR_NAME="Your Name" GIT_AUTHOR_EMAIL="you@example.com"
export GIT_COMMITTER_NAME="Your Name" GIT_COMMITTER_EMAIL="you@example.com"' -- --all
```

## 3. Deploy on Render

1. Render dashboard > **New > Blueprint** > connect the GitHub repo. Render reads `render.yaml`.
2. Fill in the prompted values:
   * `Seed__Users__0__Password` ... `Seed__Users__3__Password`: the first passwords for the four seeded users (12+ characters with upper, lower, digit and symbol). Admin is user 0. Everyone must change theirs at first sign-in.
   * `App__PublicBaseUrl`: `https://<your-service>.onrender.com`
   * `Email__*`: only if you want campaign emails to send (SMTP details). Can be left blank for now.
3. The service needs the **Starter** plan or higher, because the persistent disk (your database) is not available on the free plan.
   On a free plan the app still runs but data is lost on each deploy; you would also remove the `disk:` block.
4. After the first deploy, open **Settings > Deploy Hook**, copy the URL and add it to GitHub as the secret `RENDER_DEPLOY_HOOK_URL`.
   Set the GitHub variable `APP_URL` to your Render address. From now on every merge to `main` runs the tests and then deploys.
5. Check `https://<your-service>.onrender.com/healthz` shows `ok`, then sign in as `admin@uncoveringgreatness.co.za`.

`autoDeploy` is **off** on purpose so that only the GitHub pipeline (after green tests) can deploy. For the very first deploy Render builds
the image when the Blueprint is created.

## 4. Day-to-day

```bash
git checkout develop && git pull
git checkout -b feature/my-change
# edit, commit, push - CI runs on every push
# open a pull request into develop, then later develop -> main to release
```

## 5. If something goes wrong

| Symptom | Likely cause |
|---|---|
| Deploy fails at "Seed password ... is not acceptable" | A `Seed__Users__N__Password` secret does not meet the policy. Fix it in Render > Environment. |
| Everyone is logged out after each deploy | The disk is not mounted at `/app/App_Data`, so the login keys are regenerated. |
| Data disappears after a deploy | Same cause: no persistent disk. |
| Sign-in loops or "insecure" cookie problems | `Security__TrustForwardedHeaders` must be `true` behind Render's HTTPS proxy (already set in `render.yaml`). |
| GitHub Action "Deploy" fails at the first step | Secret `RENDER_DEPLOY_HOOK_URL` is missing. |
| Site is slow on first visit | A sleeping or restarting instance; the deploy workflow waits for `/healthz` before it reports success. |
