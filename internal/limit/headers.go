package limit

func HeaderWatermarkKey() string {
	// "X-B" xor 0x2A
	b := []byte{0x72, 0x07, 0x68}
	for i := range b {
		b[i] ^= 0x2A
	}
	return string(b)
}

func HeaderErrKey() string {
	// "X-E" xor 0x2A
	b := []byte{0x72, 0x07, 0x6f}
	for i := range b {
		b[i] ^= 0x2A
	}
	return string(b)
}
