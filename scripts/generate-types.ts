import { execFileSync } from "node:child_process";

const root = process.cwd();
const run = (args: string[]): void => {
	execFileSync("dotnet", args, { cwd: root, stdio: "inherit" });
};

run(["build", "server/MyloMail.Api", "--nologo", "-tl:off", "-clp:ErrorsOnly"]);
run([
	"tsrts",
	"--project",
	"server/MyloMail.Api/MyloMail.Api.csproj",
	"--output",
	"packages/shared-types/src/signalr",
]);
