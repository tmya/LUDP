# LUDP

LUDP is aiming to take advantage of combined UDP's fast transmission and TCP's reliability. As you probably know, RUDP is it. It was designed for the Plan 9 operating system, which is currently distributed under the MIT license. It code is written in C language so it is difficult to use with C sharp or other modern programming languages. And for a different reason, I want to develop virtual connection management systems for co-pilot mode of Microsoft Flight Simulator 2020.

## Implement functions list
- Virtual multiple channel connections. (Using for split data and command connection)
- Windowing. (incomplete)
- Acknowledgment of received packets. (incomplete)
- Retransmission of before packets regardless of state.
