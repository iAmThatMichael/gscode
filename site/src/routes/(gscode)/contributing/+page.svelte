<script lang="ts">
	import { Button } from '$lib/components/ui/button/index.js';
</script>

<svelte:head>
	<title>Contributing - GSCode</title>
	<meta name="description" content="How to contribute to the GSCode functions API" />
</svelte:head>

<article class="flex flex-col items-center w-full py-12 px-6">
	<div class="max-w-3xl w-full space-y-12 text-foreground">
		<!-- Hero -->
		<header class="space-y-4">
			<h1 class="text-3xl lg:text-4xl font-bold">Contributing to the Functions API</h1>
			<p class="text-muted-foreground text-lg">
				GSCode's IntelliSense is powered by a community-led database of GSC and CSC function
				documentation. Every function description, parameter type, and usage example you see in
				the editor comes from this API. Your contributions directly improve the experience for
				every GSCode user.
			</p>
		</header>

		<!-- Getting Started -->
		<section class="space-y-4">
			<h2 class="text-2xl font-semibold">Getting Started</h2>
			<p class="text-muted-foreground">
				All contributions are made through the web-based
				<a href="/editor" class="text-primary underline underline-offset-4 hover:text-primary/80">
					API Editor</a
				>. To begin:
			</p>
			<ol class="list-decimal list-inside space-y-2 text-muted-foreground ml-4">
				<li>
					Open the <a href="/editor" class="text-primary underline underline-offset-4 hover:text-primary/80">Editor</a> page.
				</li>
				<li>
					Click <strong class="text-foreground">Load</strong> in the sidebar.
				</li>
				<li>
					Under <strong class="text-foreground">Latest</strong>, select the library you want to
					work on &mdash; either <strong class="text-foreground">GSC</strong> or
					<strong class="text-foreground">CSC</strong>.
				</li>
			</ol>
			<p class="text-muted-foreground">
				The sidebar will populate with every function in the library. You can search and filter
				functions using the search bar (<kbd class="px-1.5 py-0.5 rounded bg-muted text-xs font-mono">Ctrl+K</kbd>).
			</p>
		</section>

		<!-- Editing a Function -->
		<section class="space-y-4">
			<h2 class="text-2xl font-semibold">Editing a Function</h2>
			<p class="text-muted-foreground">
				Click any function in the sidebar to open it in the editor. From here you can modify:
			</p>
			<ul class="list-disc list-inside space-y-2 text-muted-foreground ml-4">
				<li>
					<strong class="text-foreground">Name</strong> &mdash; the function name (should be PascalCase).
				</li>
				<li>
					<strong class="text-foreground">Description</strong> &mdash; a clear explanation of what the function does.
				</li>
				<li>
					<strong class="text-foreground">Overloads</strong> &mdash; functions can have multiple overloads,
					each with their own parameters and return type. You can add, remove, or duplicate overloads.
				</li>
				<li>
					<strong class="text-foreground">Parameters</strong> &mdash; for each overload, set the name,
					type, description, and whether the parameter is mandatory, optional, or variadic.
				</li>
				<li>
					<strong class="text-foreground">Return type</strong> &mdash; set the return type or mark
					the function as void.
				</li>
				<li>
					<strong class="text-foreground">Called on</strong> &mdash; if the function is called on an
					entity (e.g. <code class="px-1 py-0.5 rounded bg-muted text-sm font-mono">player</code>),
					specify the entity type.
				</li>
				<li>
					<strong class="text-foreground">Example</strong> &mdash; a code snippet showing how the
					function is used in practice.
				</li>
				<li>
					<strong class="text-foreground">Remarks</strong> &mdash; additional notes about behaviour,
					edge cases, or caveats.
				</li>
			</ul>
			<p class="text-muted-foreground">
				You can also create entirely new functions using the
				<strong class="text-foreground">+</strong> button in the sidebar.
			</p>
		</section>

		<!-- Conventions -->
		<section class="space-y-4">
			<h2 class="text-2xl font-semibold">Conventions</h2>
			<p class="text-muted-foreground">
				To keep the API tidy and consistent, please follow these conventions. Contributions that
				don't follow them will be delayed or refused, so please get it right the first time.
			</p>
			<ul class="list-disc list-inside space-y-4 text-muted-foreground ml-4">
				<li>
					<strong class="text-foreground">Names</strong> &mdash; function names must be PascalCase.
					Capitalise at word boundaries. For initials, prefer lowercasing subsequent letters.
					<ul class="list-none ml-6 mt-1 space-y-1 text-sm">
						<li>
							<span class="text-green-500 font-semibold">Good:</span>
							<code class="px-1 py-0.5 rounded bg-muted font-mono">IPrintLnBold</code>,
							<code class="px-1 py-0.5 rounded bg-muted font-mono">SetLpf</code>
						</li>
						<li>
							<span class="text-red-500 font-semibold">Bad:</span>
							<code class="px-1 py-0.5 rounded bg-muted font-mono">iprintlnbold</code>,
							<code class="px-1 py-0.5 rounded bg-muted font-mono">IPrintLNBold</code>,
							<code class="px-1 py-0.5 rounded bg-muted font-mono">iPrintLnBold</code>,
							<code class="px-1 py-0.5 rounded bg-muted font-mono">SetLPF</code>
						</li>
					</ul>
				</li>
				<li>
					<strong class="text-foreground">Descriptions</strong> &mdash; function, called-on,
					parameter, return type, and remarks descriptions must be statement sentences in grammatically
					correct <strong class="text-foreground">American English</strong>.
					<ul class="list-none ml-6 mt-1 space-y-1 text-sm">
						<li>
							<span class="text-green-500 font-semibold">Good:</span>
							<code class="px-1 py-0.5 rounded bg-muted font-mono">"Initializes a struct at the given location."</code>
						</li>
						<li>
							<span class="text-red-500 font-semibold">Bad:</span>
							<code class="px-1 py-0.5 rounded bg-muted font-mono">"Initialises a struct at the given location."</code>,
							<code class="px-1 py-0.5 rounded bg-muted font-mono">"Is the weapon overheating?"</code>,
							<code class="px-1 py-0.5 rounded bg-muted font-mono">"returns true if weapon has ammo"</code>
						</li>
					</ul>
				</li>
				<li>
					<strong class="text-foreground">Parameter names</strong> &mdash; parameter, Called On,
					and return value names should be camelCase.
					<ul class="list-none ml-6 mt-1 space-y-1 text-sm">
						<li>
							<span class="text-green-500 font-semibold">Good:</span>
							<code class="px-1 py-0.5 rounded bg-muted font-mono">hasAmmo</code>
						</li>
						<li>
							<span class="text-red-500 font-semibold">Bad:</span>
							<code class="px-1 py-0.5 rounded bg-muted font-mono">has_ammo</code>,
							<code class="px-1 py-0.5 rounded bg-muted font-mono">hasammo</code>,
							<code class="px-1 py-0.5 rounded bg-muted font-mono">HasAmmo</code>
						</li>
					</ul>
				</li>
			</ul>
			<p class="text-muted-foreground">
				The web editor will catch some mistakes for you, but please do not rely on it to catch everything.
			</p>
		</section>

		<!-- Verification & Validation -->
		<section class="space-y-4">
			<h2 class="text-2xl font-semibold">Verification &amp; Validation</h2>
			<p class="text-muted-foreground">
				Functions in the API are either <strong class="text-foreground">autogenerated</strong>
				(based on Treyarch's API or scraped game data, incomplete) or
				<strong class="text-foreground">verified</strong> (reviewed and confirmed by a contributor).
				These two flags are mutually exclusive.
			</p>
			<p class="text-muted-foreground">
				Marking a function as <strong class="text-foreground">verified</strong> enforces stricter
				validation rules:
			</p>
			<ul class="list-disc list-inside space-y-2 text-muted-foreground ml-4">
				<li>The description must be a proper sentence (capitalised, ending with a period).</li>
				<li>Every parameter must have a type and description.</li>
				<li>A return type must be specified (or the function explicitly marked as void).</li>
				<li>Remarks, if present, must also be proper sentences.</li>
			</ul>
			<p class="text-muted-foreground">
				The right sidebar of the editor shows validation errors in real-time. Resolve all errors
				before marking a function as verified.
			</p>
		</section>

		<!-- Submitting Changes -->
		<section class="space-y-4">
			<h2 class="text-2xl font-semibold">Submitting Your Changes</h2>
			<p class="text-muted-foreground">
				Once you've finished editing, export your work:
			</p>
			<ol class="list-decimal list-inside space-y-2 text-muted-foreground ml-4">
				<li>
					Click <strong class="text-foreground">Download</strong> in the sidebar to export the
					library as a JSON file.
				</li>
				<li>
					Open a new issue on the
					<a
						href="https://github.com/blakintosh/gscode/issues/new"
						class="text-primary underline underline-offset-4 hover:text-primary/80"
						target="_blank"
						rel="noopener noreferrer"
					>
						GSCode GitHub repository</a
					>.
				</li>
				<li>
					Attach the downloaded JSON file to the issue and briefly describe what you changed.
				</li>
			</ol>
			<p class="text-muted-foreground">
				A maintainer will review your changes and merge them into the next API revision.
			</p>
		</section>

		<!-- CTA -->
		<section class="flex flex-col items-center gap-4 pt-4">
			<Button href="/editor" size="lg" class="text-lg px-8">
				Open the Editor
			</Button>
		</section>
	</div>
</article>
