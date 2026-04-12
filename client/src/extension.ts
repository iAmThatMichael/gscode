/* --------------------------------------------------------------------------------------------
 * Copyright (c) Microsoft Corporation. All rights reserved.
 * Licensed under the MIT License. See License.txt in the project root for license information.
 * ------------------------------------------------------------------------------------------ */
// tslint:disable
"use strict";

import * as path from "path";
import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';

let pendingReload = false;
import { execFile } from "child_process";

import { workspace, ExtensionContext, window } from "vscode";
import {
    LanguageClientOptions,
    ServerOptions,
    TransportKind,
} from "vscode-languageclient/node";
import dotenv = require("dotenv");

const REQUIRED_DOTNET_MAJOR = 10;
const DOTNET_DOWNLOAD_URL = "https://dotnet.microsoft.com/download/dotnet/10.0";

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

let client: LanguageClient;

function checkLanguageMismatch(document: vscode.TextDocument): void {
    const ext = path.extname(document.fileName).toLowerCase();
    if (ext !== ".gsc" && ext !== ".csc") return;

    const expectedLang = ext === ".gsc" ? "gsc" : "csc";
    if (document.languageId === expectedLang) return;

    const config = workspace.getConfiguration("gscode");
    if (!config.get<boolean>("warnLanguageMismatch", true)) return;

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
  const workspaceIndexingMode = config.get<string>("workspaceIndexingMode", "off");
  const serverLogLevel = config.get<string>("serverLogLevel", "off");
  const customRawPath = config.get<string>("customRawPath");
  const allowRawFolderWrites = config.get<boolean>("allowRawFolderWrites", false);

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
      configurationSection: 'gscode',
    },
    // Pass configuration via initializationOptions to avoid workspace/configuration request
    initializationOptions: {
      gscode: {
        workspaceIndexingMode: workspaceIndexingMode,
        serverLogLevel: serverLogLevel,
        customRawPath: customRawPath,
        allowRawFolderWrites: allowRawFolderWrites,
      },
    },
    outputChannel: window.createOutputChannel('GSCode Language Server'),
    middleware: {
      // Suppress diagnostics for files outside the workspace (e.g. raw folder files)
      handleDiagnostics(uri, diagnostics, next) {
        next(uri, diagnostics);
      },

      // Add extra context to completion resolve before handing back to VS Code
      resolveCompletionItem(item, token, next) {
        return next(item, token);
      },
    },
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
  client.start().then(() => {
    client.onNotification('gscode/rawFolderWriteWarning', async () => {
      const cfg = workspace.getConfiguration('gscode');
      if (cfg.get<boolean>('allowRawFolderWrites', false)) return;

      const action = await window.showWarningMessage(
        'You are editing a file in a protected raw folder. Consider working in a separate mod directory to avoid modifying vanilla game files.',
        'Dismiss',
        "Don't show again"
      );
      if (action === "Don't show again") {
        await cfg.update('allowRawFolderWrites', true, vscode.ConfigurationTarget.Global);
      }
    });
  });

  // Check already-open documents for language mismatch
  vscode.workspace.textDocuments.forEach(checkLanguageMismatch);
  context.subscriptions.push(
    vscode.workspace.onDidOpenTextDocument(checkLanguageMismatch)
  );

  // Watch for configuration changes and send to server
  context.subscriptions.push(
    vscode.workspace.onDidChangeConfiguration(async e => {
      if (e.affectsConfiguration('gscode')) {
        // Send the entire gscode configuration section to the server
        const config = vscode.workspace.getConfiguration('gscode');
        client.sendNotification('workspace/didChangeConfiguration', {
          settings: {
            gscode: {
              customRawPath: config.get('customRawPath'),
              allowRawFolderWrites: config.get('allowRawFolderWrites'),
              workspaceIndexingMode: config.get('workspaceIndexingMode')
            }
          }
        });

        // For certain settings that require reload, prompt the user
        if (e.affectsConfiguration('gscode.serverLogLevel') ||
            e.affectsConfiguration('gscode.workspaceIndexingMode') ||
            e.affectsConfiguration('gscode.customRawPath')) {

          // Prevent multiple prompts
          if (pendingReload) {
            return;
          }
          pendingReload = true;

          const action = await vscode.window.showInformationMessage(
            'GSCode configuration changed. Reload window for changes to take effect.',
            'Reload Window',
            'Later'
          );

          if (action === 'Reload Window') {
            await vscode.commands.executeCommand('workbench.action.reloadWindow');
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
