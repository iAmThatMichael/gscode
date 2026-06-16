// tslint:disable
"use strict";

import * as path from "path";
import * as vscode from "vscode";
import { execFile } from "child_process";
import { workspace, ExtensionContext, window } from "vscode";
import {
    LanguageClient,
    LanguageClientOptions,
    ServerOptions,
    TransportKind,
} from "vscode-languageclient/node";
import * as dotenv from "dotenv";

const REQUIRED_DOTNET_MAJOR = 10;
const DOTNET_DOWNLOAD_URL = "https://dotnet.microsoft.com/download/dotnet/10.0";

let pendingReload = false;
let client: LanguageClient;

function isDotnetRuntimeAvailable(): Promise<boolean> {
    return new Promise((resolve) => {
        execFile("dotnet", ["--list-runtimes"], { encoding: "utf-8" }, (error, stdout) => {
            if (error) {
                resolve(false);
                return;
            }
            const pattern = new RegExp(`Microsoft\\.NETCore\\.App ${REQUIRED_DOTNET_MAJOR}\\.`);
            resolve(pattern.test(stdout));
        });
    });
}

function checkLanguageMismatch(document: vscode.TextDocument): void {
    const ext = path.extname(document.fileName).toLowerCase();
    if (ext !== ".gsc" && ext !== ".csc") { return; }

    const expectedLang = ext === ".gsc" ? "gsc" : "csc";
    if (document.languageId === expectedLang) { return; }

    const config = workspace.getConfiguration("gscode");
    if (!config.get<boolean>("warnLanguageMismatch", true)) { return; }

    window.showWarningMessage(
        `'${path.basename(document.fileName)}' has a ${ext} extension but is set to ${document.languageId.toUpperCase()} language mode. GSCode features may not work correctly.`,
        "Fix Language Mode",
        "Dismiss"
    ).then(action => {
        if (action === "Fix Language Mode") {
            vscode.languages.setTextDocumentLanguage(document, expectedLang);
        }
    });
}

export async function activate(context: ExtensionContext) {
    // Ensure the user has .NET 10 installed, otherwise there isn't a whole lot we can do.
    if (!await isDotnetRuntimeAvailable()) {
        window
            .showErrorMessage(
                `GSCode requires the .NET ${REQUIRED_DOTNET_MAJOR} runtime to run. Please install it and reload the window.`,
                "Download .NET",
                "Dismiss"
            )
            .then((selection) => {
                if (selection === "Download .NET") {
                    vscode.env.openExternal(vscode.Uri.parse(DOTNET_DOWNLOAD_URL));
                }
            });
        return;
    }

    const serverExe = "dotnet";
    dotenv.config({ path: path.join(context.extensionPath, ".env") });

    // Flip SHOULD_TEST_IN_RELEASE to use the release build path during testing, e.g. for performance profiling.
    const testServerLocation = process.env.SHOULD_TEST_IN_RELEASE === "true"
        ? process.env.SERVER_LOCATION
        : process.env.DEBUG_SERVER_LOCATION;

    const serverLocation = process.env.VSCODE_DEBUG
        ? testServerLocation
        : "service";
    if (!serverLocation) {
        throw new Error(
            "DEBUG_SERVER_LOCATION environment variable is not set. Please set it to the location of the GSCode.NET Language Server in .env"
        );
    }

    const serverDll = context.asAbsolutePath(
        path.normalize(path.join(serverLocation, "GSCode.NET.dll"))
    );

    // If the extension is launched in debug mode then the debug server options are used
    // Otherwise the run options are used
    const serverOptions: ServerOptions = {
        run: {
            command: serverExe,
            transport: TransportKind.pipe,
            args: [serverDll],
        },
        debug: {
            command: serverExe,
            transport: TransportKind.pipe,
            args: [serverDll],
        },
    };

    const gscWatcher = workspace.createFileSystemWatcher("**/*.gsc");
    const cscWatcher = workspace.createFileSystemWatcher("**/*.csc");
    const gshWatcher = workspace.createFileSystemWatcher("**/*.gsh");

    // Get configuration from workspace settings
    const config = workspace.getConfiguration("gscode");
    const workspaceIndexingMode = config.get<string>("workspaceIndexingMode", "off");
    const serverLogLevel = config.get<string>("serverLogLevel", "off");
    const customRawPath = config.get<string>("customRawPath");
    const allowRawFolderWrites = config.get<boolean>("allowRawFolderWrites", false);
    const enableWorkspaceCache = config.get<boolean>("enableWorkspaceCache", false);
    const indexGameScripts = config.get<boolean>("indexGameScripts", true);
    const rawFileWarningMode = config.get<string>("rawFileWarningMode", "stock");

    // Options to control the language client
    const clientOptions: LanguageClientOptions = {
        documentSelector: [
            { scheme: "file", language: "gsc", pattern: "**/*.gsc" },
            { scheme: "file", language: "csc", pattern: "**/*.csc" },
            { scheme: "file", language: "gsh", pattern: "**/*.gsh" },
        ],
        progressOnInitialization: true,
        synchronize: {
            fileEvents: [gscWatcher, cscWatcher, gshWatcher],
            configurationSection: "gscode",
        },
        // Pass configuration via initializationOptions to avoid workspace/configuration request
        initializationOptions: {
            gscode: {
                workspaceIndexingMode,
                serverLogLevel,
                customRawPath,
                allowRawFolderWrites,
                enableWorkspaceCache,
                indexGameScripts,
                rawFileWarningMode,
            },
        },
        outputChannel: window.createOutputChannel("GSCode Language Server"),
    };

    // Create the language client and start the client.
    client = new LanguageClient(
        "gscode",
        "GSCode Language Server",
        serverOptions,
        clientOptions
    );

    // Push the disposable to the context's subscriptions so that the
    // client can be deactivated on extension deactivation
    await client.start();

    // Status bar item — only shown when workspace indexing is active
    if (workspaceIndexingMode !== "off") {
        const outputChannel = clientOptions.outputChannel as vscode.OutputChannel;

        const statusBar = window.createStatusBarItem(vscode.StatusBarAlignment.Left, 0);
        statusBar.name = "GSCode Indexing";
        statusBar.command = "gscode.showOutput";
        context.subscriptions.push(statusBar);

        context.subscriptions.push(
            vscode.commands.registerCommand("gscode.showOutput", () => outputChannel?.show())
        );

        statusBar.text = "$(loading~spin) GSCode: Indexing...";
        statusBar.tooltip = "GSCode is indexing workspace scripts";
        statusBar.show();

        client.onNotification("gscode/indexingStarted", (params: { totalFiles: number }) => {
            statusBar.text = `$(loading~spin) GSCode: Indexing (0 / ${params.totalFiles})`;
            statusBar.tooltip = `Indexing ${params.totalFiles} script files...`;
            statusBar.show();
        });

        client.onNotification("gscode/indexingProgress", (params: { filesIndexed: number; totalFiles: number }) => {
            statusBar.text = `$(loading~spin) GSCode: Indexing (${params.filesIndexed} / ${params.totalFiles})`;
        });

        client.onNotification("gscode/indexingComplete", (params: { filesIndexed: number; totalFiles: number; fromCache: number }) => {
            const parsed = params.filesIndexed - params.fromCache;
            statusBar.text = `$(check) GSCode: Ready`;
            statusBar.tooltip = `Indexed ${params.filesIndexed} files (${parsed} parsed, ${params.fromCache} from cache)`;
        });
    }

    // Handle raw folder write warnings sent by the server. The server decides whether the
    // file warrants a warning (per gscode.rawFileWarningMode); the client just displays it.
    client.onNotification("gscode/rawFolderWriteWarning", async (params: { path: string }) => {
        const action = await window.showWarningMessage(
            `You are saving '${path.basename(params.path)}' inside a protected raw folder. Consider working in a separate mod directory to avoid modifying vanilla game files.`,
            "Dismiss",
            "Don't show again"
        );
        if (action === "Don't show again") {
            const cfg = workspace.getConfiguration("gscode");
            await cfg.update("rawFileWarningMode", "off", vscode.ConfigurationTarget.Global);
        }
    });

    // Check already-open documents for language mismatch
    vscode.workspace.textDocuments.forEach(checkLanguageMismatch);
    context.subscriptions.push(
        vscode.workspace.onDidOpenTextDocument(checkLanguageMismatch)
    );

    // Watch for configuration changes and send to server
    context.subscriptions.push(
        vscode.workspace.onDidChangeConfiguration(async e => {
            if (e.affectsConfiguration("gscode")) {
                // Send the entire gscode configuration section to the server
                const updatedConfig = vscode.workspace.getConfiguration("gscode");
                client.sendNotification("workspace/didChangeConfiguration", {
                    settings: {
                        gscode: {
                            customRawPath: updatedConfig.get("customRawPath"),
                            allowRawFolderWrites: updatedConfig.get("allowRawFolderWrites"),
                            workspaceIndexingMode: updatedConfig.get("workspaceIndexingMode"),
                            serverLogLevel: updatedConfig.get("serverLogLevel"),
                            enableWorkspaceCache: updatedConfig.get("enableWorkspaceCache"),
                            indexGameScripts: updatedConfig.get("indexGameScripts"),
                            rawFileWarningMode: updatedConfig.get("rawFileWarningMode"),
                        },
                    },
                });

                // For certain settings that require reload, prompt the user
                if (
                    e.affectsConfiguration("gscode.serverLogLevel") ||
                    e.affectsConfiguration("gscode.workspaceIndexingMode") ||
                    e.affectsConfiguration("gscode.customRawPath") ||
                    e.affectsConfiguration("gscode.indexGameScripts")
                ) {
                    // Prevent multiple prompts
                    if (pendingReload) {
                        return;
                    }
                    pendingReload = true;

                    const action = await vscode.window.showInformationMessage(
                        "GSCode configuration changed. Reload window for changes to take effect.",
                        "Reload Window",
                        "Later"
                    );

                    if (action === "Reload Window") {
                        await vscode.commands.executeCommand("workbench.action.reloadWindow");
                    } else {
                        pendingReload = false;
                    }
                }
            }
        })
    );
}

export function deactivate(): Thenable<void> | undefined {
    if (!client) {
        return undefined;
    }
    return client.stop();
}