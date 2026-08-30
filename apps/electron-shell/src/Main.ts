export const backendMode =
	process.env.ELECTRON_BACKEND_MODE === "attach" ? "attach" : "spawn";
