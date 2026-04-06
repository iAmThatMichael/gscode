<script lang="ts">
	import type { Snippet } from 'svelte';

	interface Props {
		items: any[];
		itemHeight: number;
		height?: string;
		children: Snippet<[{ item: any; index: number }]>;
	}

	let { items, itemHeight, height = '100%', children }: Props = $props();

	let viewport: HTMLDivElement | undefined = $state();
	let viewportHeight = $state(0);
	let scrollTop = $state(0);

	let totalHeight = $derived(items.length * itemHeight);
	let startIndex = $derived(Math.floor(scrollTop / itemHeight));
	let visibleCount = $derived(Math.ceil(viewportHeight / itemHeight) + 1);
	let endIndex = $derived(Math.min(startIndex + visibleCount, items.length));
	let offsetY = $derived(startIndex * itemHeight);

	let visibleItems = $derived(
		items.slice(startIndex, endIndex).map((item, i) => ({
			item,
			index: startIndex + i
		}))
	);

	function handleScroll() {
		if (viewport) {
			scrollTop = viewport.scrollTop;
		}
	}
</script>

<div
	bind:this={viewport}
	bind:offsetHeight={viewportHeight}
	onscroll={handleScroll}
	class="relative overflow-y-auto"
	style="height: {height};"
>
	<div style="height: {totalHeight}px;">
		<div style="transform: translateY({offsetY}px);">
			{#each visibleItems as entry (entry.index)}
				{@render children(entry)}
			{/each}
		</div>
	</div>
</div>
