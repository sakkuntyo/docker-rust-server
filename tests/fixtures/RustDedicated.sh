#!/bin/bash
# Mock producer only; copied to ./RustDedicated by the test harness.
printf '%s\n' 'stdout preserved'
printf '%s\n' 'stderr preserved' >&2
if [[ "${URL_TEST_CASE:-normal}" != image-only ]]; then
  printf '%s\n' '[Rust.MapCache] Map uploaded to backend: https://files.facepunch.com/rust/maps/aaaa/proceduralmap.2700.0.288_aaaa.map'
fi
sleep 0.1
if [[ "${URL_TEST_CASE:-normal}" != map-only ]]; then
  printf '%s\n' 'Image uploaded to backend: https://files.facepunch.com/rust/map-images/bbbb/proceduralmap.2700.0.288_bbbb.jpg'
  printf '%s\n' '[Rust.MapCache] Map uploaded to backend: https://files.facepunch.com/rust/maps/cccc/second.map' >&2
  printf '%s\n' 'Image uploaded to backend: https://files.facepunch.com/rust/map-images/dddd/second.png' >&2
  printf '%s\n' 'Image uploaded to backend: https://files.facepunch.com/rust/map-images/eeee/third.jpeg'
fi
printf '%s\n' 'unrelated https://example.com/not-a-map.jpg'
sleep 0.2
