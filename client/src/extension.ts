/* --------------------------------------------------------------------------------------------
 * Copyright (c) Microsoft Corporation. All rights reserved.
 * Licensed under the MIT License. See License.txt in the project root for license information.
 * ------------------------------------------------------------------------------------------ */
// tslint:disable
"use strict";

import * as path from "path";
import * as vscode from 'vscode';
import { execSync } from "child_process";

import { workspace, Disposable, ExtensionContext, window } from "vscode";
import {
    LanguageClient,
    LanguageClientOptions,
    SettingMonitor,
    ServerOptions,
    TransportKind,
    InitializeParams,
    StreamInfo,
    createServerPipeTransport,
} from "vscode-languageclient/node";
import { Trace, createClientPipeTransport } from "vscode-jsonrpc/node";
import { createConnection } from "net";
import dotenv = require("dotenv");

const REQUIRED_DOTNET_MAJOR = 10;
const DOTNET_DOWNLOAD_URL = "https://dotnet.microsoft.com/download/dotnet/10.0";

function isDotnetRuntimeAvailable(): boolean {
    try {
        const output = execSync("dotnet --list-runtimes", { encoding: "utf-8" });
        // Look for Microsoft.NETCore.App with the required major version
        const pattern = new RegExp(`Microsoft\\.NETCore\\.App ${REQUIRED_DOTNET_MAJOR}\\.`);
        return pattern.test(output);
    } catch {
        return false;
    }
}

let client: LanguageClient;

export function activate(context: ExtensionContext) {
    // Ensure the user has .NET 10 installed, otherwise there isn't a whole lot we can do.
    if (!isDotnetRuntimeAvailable()) {
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

    // The server is implemented in node
    let serverExe = "dotnet";

    dotenv.config({ path: path.join(context.extensionPath, ".env") });

    // Flip SHOULD_TEST_IN_RELEASE to use the release build path during testing, e.g. for performance profiling.
    const testServerLocation = process.env.SHOULD_TEST_IN_RELEASE === "true" ? process.env.RELEASE_SERVER_LOCATION : process.env.DEBUG_SERVER_LOCATION;

    const serverLocation = process.env.VSCODE_DEBUG
        ? testServerLocation
        : "service";
    if (!serverLocation) {
        throw new Error(
            "DEBUG_SERVER_LOCATION environment variable is not set. Please set it to the location of the GSCode.NET Language Server in .env"
        );
    }

    console.log(
        context.asAbsolutePath(
            path.normalize(path.join(serverLocation, "GSCode.NET.dll"))
        )
    );

    // If the extension is launched in debug mode then the debug server options are used
    // Otherwise the run options are used
    let serverOptions: ServerOptions = {
        // run: { command: serverExe, args: ['-lsp', '-d'] },
        run: {
            command: serverExe,
            transport: TransportKind.pipe,
            // args: [serverModule],
            args: [
                context.asAbsolutePath(
                    path.normalize(path.join(serverLocation, "GSCode.NET.dll"))
                ),
            ],
            // args: [path.join(serverLocation, 'GSCode.NET.dll')],
        },
        // debug: { command: serverExe, args: ['-lsp', '-d'] }
        debug: {
            command: serverExe,
            transport: TransportKind.pipe,
            // args: [serverModule],
            args: [
                context.asAbsolutePath(
                    path.normalize(path.join(serverLocation, "GSCode.NET.dll"))
                ),
            ],
            // args: [path.join(serverLocation, 'GSCode.NET.exe')],
        },
    };

    const gscWatcher = workspace.createFileSystemWatcher("**/*.gsc");
    const cscWatcher = workspace.createFileSystemWatcher("**/*.csc");

    // Get configuration from workspace settings
    const config = workspace.getConfiguration("gscode");
    const enableWorkspaceIndexing = config.get<boolean>("enableWorkspaceIndexing", false);

    // Options to control the language client
    let clientOptions: LanguageClientOptions = {
        documentSelector: [
            {
                scheme: "file",
                language: "gsc",
                pattern: "**/*.gsc",
            },
            {
                scheme: "file",
                language: "csc",
                pattern: "**/*.csc",
            },
        ],
        progressOnInitialization: true,
        synchronize: {
            fileEvents: [gscWatcher, cscWatcher],
        },
        // Pass configuration via initializationOptions to avoid workspace/configuration request
        initializationOptions: {
            gscode: {
                enableWorkspaceIndexing: enableWorkspaceIndexing,
            },
        },
        outputChannel: window.createOutputChannel('GSCode Language Server'),
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
    client.start();

    context.subscriptions.push(vscode.workspace.onDidChangeConfiguration(e => {
        if (e.affectsConfiguration('gscode.enableWorkspaceIndexing')) {
            vscode.window.showInformationMessage(
                'Changing gscode.enableWorkspaceIndexing requires reloading the GSCode language server.',
                'Reload'
            ).then(sel => {
                if (sel === 'Reload') {
                    vscode.commands.executeCommand('workbench.action.reloadWindow');
                }
            });
        }
    }));
}

export function deactivate(): Thenable<void> | undefined {
    if (!client) {
        return undefined;
    }
    return client.stop();
}
